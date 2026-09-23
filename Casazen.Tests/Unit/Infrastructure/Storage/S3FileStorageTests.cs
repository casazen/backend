using System.Net;
using System.Web;
using Amazon.S3;
using Amazon.S3.Model;
using Casazen.Core.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure.Storage;

/// <summary>FD-07: the Supabase (S3 API) adapter against a mocked <see cref="IAmazonS3"/>.</summary>
public class S3FileStorageTests
{
    private const string PublicBaseUrl = "https://ref.supabase.co/storage/v1/object/public/casazen-test-public";

    private readonly Mock<IAmazonS3> _s3 = new(MockBehavior.Strict);
    private readonly S3FileStorage _storage;

    public S3FileStorageTests()
    {
        _storage = new S3FileStorage(_s3.Object, Options.Create(CreateOptions()), NullLogger<S3FileStorage>.Instance);
    }

    private static StorageOptions CreateOptions() => new()
    {
        Provider = StorageOptions.S3Provider,
        PublicBaseUrl = PublicBaseUrl,
        S3 = new S3StorageOptions
        {
            ServiceUrl = "https://ref.storage.supabase.co/storage/v1/s3",
            Region = "eu-central-1",
            AccessKeyId = "test-access-key",
            SecretAccessKey = "test-secret-key",
            PublicBucket = "casazen-test-public",
            PrivateBucket = "casazen-test-private",
        },
    };

    [Fact]
    public async Task PutAsync_PrivateObject_SendsSignedNonChunkedPutToPrivateBucket()
    {
        PutObjectRequest? sent = null;
        byte[]? sentBytes = null;
        _s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) =>
            {
                sent = request;
                sentBytes = ((MemoryStream)request.InputStream).ToArray();
            })
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        await _storage.PutAsync(StorageBucket.Private, "properties/p1/documents/a.pdf", new NonSeekableStream([1, 2, 3]), "application/pdf");

        Assert.NotNull(sent);
        Assert.Equal("casazen-test-private", sent!.BucketName);
        Assert.Equal("properties/p1/documents/a.pdf", sent.Key);
        Assert.Equal("application/pdf", sent.ContentType);
        Assert.False(sent.UseChunkEncoding);
        Assert.Equal(new byte[] { 1, 2, 3 }, sentBytes);
    }

    [Fact]
    public async Task PutAsync_PublicObject_UsesPublicBucket()
    {
        _s3.Setup(s => s.PutObjectAsync(It.Is<PutObjectRequest>(r => r.BucketName == "casazen-test-public"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());

        await _storage.PutAsync(StorageBucket.Public, "properties/p1/photos/a.jpg", new MemoryStream([1]), "image/jpeg");

        _s3.VerifyAll();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/uploads/properties/a.jpg")]
    [InlineData("properties/a b.jpg")]
    public async Task PutAsync_InvalidKey_ThrowsWithoutCallingS3(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.PutAsync(StorageBucket.Public, key, new MemoryStream([1]), "image/jpeg"));
    }

    [Fact]
    public async Task OpenReadAsync_ExistingObject_ReturnsSeekableCopyOfContent()
    {
        _s3.Setup(s => s.GetObjectAsync("casazen-test-private", "guest-documents/o/g/id.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = new MemoryStream([9, 8, 7]) });

        await using var stream = await _storage.OpenReadAsync(StorageBucket.Private, "guest-documents/o/g/id.pdf");

        Assert.NotNull(stream);
        Assert.True(stream!.CanSeek);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(new byte[] { 9, 8, 7 }, copy.ToArray());
    }

    [Fact]
    public async Task OpenReadAsync_MissingObject_ReturnsNull()
    {
        _s3.Setup(s => s.GetObjectAsync("casazen-test-private", "properties/p/documents/missing.pdf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not found") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" });

        Assert.Null(await _storage.OpenReadAsync(StorageBucket.Private, "properties/p/documents/missing.pdf"));
    }

    [Fact]
    public async Task OpenReadAsync_ServiceError_Propagates()
    {
        _s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Access denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" });

        await Assert.ThrowsAsync<AmazonS3Exception>(() => _storage.OpenReadAsync(StorageBucket.Private, "properties/p/documents/a.pdf"));
    }

    [Fact]
    public async Task OpenReadAsync_InvalidKey_ReturnsNullWithoutCallingS3()
    {
        Assert.Null(await _storage.OpenReadAsync(StorageBucket.Private, "/uploads/properties/p/documents/a.pdf"));
    }

    [Fact]
    public async Task ExistsAsync_ReflectsObjectMetadata()
    {
        _s3.Setup(s => s.GetObjectMetadataAsync("casazen-test-public", "properties/p/photos/here.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse());
        _s3.Setup(s => s.GetObjectMetadataAsync("casazen-test-public", "properties/p/photos/gone.jpg", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not found") { StatusCode = HttpStatusCode.NotFound });

        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, "properties/p/photos/here.jpg"));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, "properties/p/photos/gone.jpg"));
    }

    [Fact]
    public async Task DeleteAsync_DeletesFromRequestedBucket()
    {
        _s3.Setup(s => s.DeleteObjectAsync("casazen-test-private", "properties/p/documents/a.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        await _storage.DeleteAsync(StorageBucket.Private, "properties/p/documents/a.pdf");

        _s3.VerifyAll();
    }

    [Fact]
    public void GetPublicUrl_ReturnsAbsoluteUrlUnderPublicBaseUrl()
    {
        var url = _storage.GetPublicUrl("properties/p1/photos/a.jpg");

        Assert.Equal($"{PublicBaseUrl}/properties/p1/photos/a.jpg", url);
    }

    [Fact]
    public void TryGetPublicKey_RoundTripsOwnUrlsAndRejectsOthers()
    {
        var url = _storage.GetPublicUrl("suppliers/o1/photos/b.png");

        Assert.Equal("suppliers/o1/photos/b.png", _storage.TryGetPublicKey(url));
        Assert.Null(_storage.TryGetPublicKey("/uploads/properties/p1/a.jpg"));
        Assert.Null(_storage.TryGetPublicKey("https://evil.example/storage/v1/object/public/casazen-test-public/a.jpg"));
        Assert.Null(_storage.TryGetPublicKey($"{PublicBaseUrl}/../casazen-test-private/x.pdf"));
    }

    [Fact]
    public async Task GetSignedReadUrlAsync_SignsGetOnPrivateBucketWithLifetimeAndAttachmentName()
    {
        GetPreSignedUrlRequest? sent = null;
        _s3.Setup(s => s.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => sent = r)
            .ReturnsAsync("https://ref.storage.supabase.co/storage/v1/s3/casazen-test-private/k.pdf?X-Amz-Signature=x");

        var url = await _storage.GetSignedReadUrlAsync("properties/p/documents/k.pdf", TimeSpan.FromMinutes(5), "Contratto è.pdf");

        Assert.NotNull(url);
        Assert.NotNull(sent);
        Assert.Equal("casazen-test-private", sent!.BucketName);
        Assert.Equal("properties/p/documents/k.pdf", sent.Key);
        Assert.Equal(HttpVerb.GET, sent.Verb);
        Assert.Equal(Protocol.HTTPS, sent.Protocol);
        Assert.InRange(sent.Expires!.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(5));
        Assert.StartsWith("attachment;", sent.ResponseHeaderOverrides.ContentDisposition);
        Assert.Contains("filename*=utf-8''Contratto%20%C3%A8.pdf", sent.ResponseHeaderOverrides.ContentDisposition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSignedReadUrlAsync_RealClient_KeepsSupabasePathPrefixAndExpiry()
    {
        // No network: presigning is a local computation. Guards the Supabase endpoint layout
        // (path-style, /storage/v1/s3 prefix preserved) that the SDK must produce.
        var options = CreateOptions();
        using var client = S3FileStorage.CreateClient(options);
        var storage = new S3FileStorage(client, Options.Create(options), NullLogger<S3FileStorage>.Instance);

        var url = await storage.GetSignedReadUrlAsync("properties/p/documents/k.pdf", TimeSpan.FromMinutes(5), "k.pdf");

        Assert.NotNull(url);
        Assert.Equal("https", url!.Scheme);
        Assert.Equal("ref.storage.supabase.co", url.Host);
        Assert.Equal("/storage/v1/s3/casazen-test-private/properties/p/documents/k.pdf", url.AbsolutePath);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("300", query["X-Amz-Expires"]);
        Assert.Contains("eu-central-1", query["X-Amz-Credential"]);
        Assert.False(string.IsNullOrEmpty(query["X-Amz-Signature"]));
    }

    [Fact]
    public void CreateClient_ConfiguresSupabaseEndpoint()
    {
        using var client = S3FileStorage.CreateClient(CreateOptions());

        var config = (AmazonS3Config)client.Config;
        Assert.True(config.ForcePathStyle);
        Assert.Equal("eu-central-1", config.AuthenticationRegion);
        Assert.StartsWith("https://ref.storage.supabase.co/storage/v1/s3", config.ServiceURL);
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
