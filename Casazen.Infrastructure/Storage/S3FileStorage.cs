using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Storage;

/// <summary>
/// <see cref="IFileStorage"/> on an S3-compatible service: Supabase Storage in the deployed
/// environments (decision D8). Two buckets: a public-read one for photos and a private one for
/// documents, never exposed except through signed URLs or authenticated endpoints.
/// </summary>
public sealed class S3FileStorage(
    IAmazonS3 s3,
    IOptions<StorageOptions> options,
    ILogger<S3FileStorage> logger) : IFileStorage
{
    private readonly StorageOptions _options = options.Value;

    /// <summary>
    /// S3 client for Supabase: explicit endpoint, path-style addressing and checksums only when the
    /// operation requires them (S3-compatible services do not all accept the aws-chunked trailers
    /// the SDK sends by default).
    /// </summary>
    public static IAmazonS3 CreateClient(StorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.S3.ServiceUrl,
            AuthenticationRegion = options.S3.Region,
            ForcePathStyle = options.S3.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        var credentials = new BasicAWSCredentials(options.S3.AccessKeyId, options.S3.SecretAccessKey);
        return new AmazonS3Client(credentials, config);
    }

    public async Task PutAsync(
        StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        StorageKeys.EnsureValid(key);

        // Uploads are small (<= 10 MB): buffering gives the SDK a seekable body, so it can send a
        // plain signed PUT with Content-Length instead of a streaming (chunked) signature.
        await using var body = await ToSeekableAsync(content, cancellationToken);
        var request = new PutObjectRequest
        {
            BucketName = BucketName(bucket),
            Key = key,
            InputStream = body,
            ContentType = contentType,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        };

        await s3.PutObjectAsync(request, cancellationToken);
        logger.LogInformation("Stored object {Key} in {Bucket} bucket ({Bytes} bytes)", key, bucket, body.Length);
    }

    public async Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        if (!StorageKeys.IsValid(key))
            return null;

        try
        {
            using var response = await s3.GetObjectAsync(BucketName(bucket), key, cancellationToken);
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        if (!StorageKeys.IsValid(key))
            return false;

        try
        {
            await s3.GetObjectMetadataAsync(BucketName(bucket), key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    public async Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        StorageKeys.EnsureValid(key);
        await s3.DeleteObjectAsync(BucketName(bucket), key, cancellationToken);
        logger.LogInformation("Deleted object {Key} from {Bucket} bucket", key, bucket);
    }

    public string GetPublicUrl(string key) =>
        PublicUrls.Build(_options.PublicBaseUrl!, StorageKeys.EnsureValid(key));

    public string? TryGetPublicKey(string url) => PublicUrls.TryGetKey(_options.PublicBaseUrl!, url);

    public async Task<Uri?> GetSignedReadUrlAsync(
        string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default)
    {
        StorageKeys.EnsureValid(key);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = BucketName(StorageBucket.Private),
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = new Uri(_options.S3.ServiceUrl!).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS,
        };
        if (!string.IsNullOrWhiteSpace(downloadFileName))
            request.ResponseHeaderOverrides.ContentDisposition = ContentDisposition.Attachment(downloadFileName);

        cancellationToken.ThrowIfCancellationRequested();
        return new Uri(await s3.GetPreSignedURLAsync(request));
    }

    private string BucketName(StorageBucket bucket) => bucket switch
    {
        StorageBucket.Public => _options.S3.PublicBucket!,
        StorageBucket.Private => _options.S3.PrivateBucket!,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };

    private static bool IsNotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
        || string.Equals(ex.ErrorCode, "NotFound", StringComparison.Ordinal);

    private static async Task<MemoryStream> ToSeekableAsync(Stream content, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return buffer;
    }
}
