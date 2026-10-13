using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Storage;

/// <summary>
/// Object storage configuration (section <c>Storage</c>, env vars <c>Storage__*</c>).
/// Runbook: <c>docs/runbooks/storage.md</c>.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public const string S3Provider = "S3";
    public const string FileSystemProvider = "FileSystem";

    /// <summary>
    /// <c>S3</c> (Supabase Storage, required outside Development/Testing) or <c>FileSystem</c>
    /// (Development and tests only). Empty: FileSystem in Development/Testing, S3 elsewhere.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Absolute base URL of the public bucket; a public object is served at <c>{PublicBaseUrl}/{key}</c>.
    /// Supabase: <c>https://&lt;ref&gt;.supabase.co/storage/v1/object/public/&lt;public-bucket&gt;</c>.
    /// FileSystem default: <c>{App:ApiBaseUrl}/storage/public</c>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Lifetime of signed URLs of private objects, in minutes (1-60).</summary>
    public int SignedUrlTtlMinutes { get; set; } = 5;

    public S3StorageOptions S3 { get; set; } = new();

    public FileSystemStorageOptions FileSystem { get; set; } = new();

    public bool UsesFileSystem => string.Equals(Provider, FileSystemProvider, StringComparison.OrdinalIgnoreCase);

    public bool UsesS3 => string.Equals(Provider, S3Provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>True in the environments where the filesystem provider is allowed.</summary>
    public static bool IsLocalEnvironment(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}

public sealed class S3StorageOptions
{
    /// <summary>S3 endpoint. Supabase: <c>https://&lt;ref&gt;.storage.supabase.co/storage/v1/s3</c>.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Region shown in Supabase (Project Settings → Storage → S3 connection), e.g. <c>eu-central-1</c>.</summary>
    public string? Region { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    /// <summary>Public-read bucket (photos).</summary>
    public string? PublicBucket { get; set; }

    /// <summary>Private bucket (documents, guest ID scans, contracts).</summary>
    public string? PrivateBucket { get; set; }

    /// <summary>Path-style addressing (<c>{endpoint}/{bucket}/{key}</c>), required by Supabase.</summary>
    public bool ForcePathStyle { get; set; } = true;
}

public sealed class FileSystemStorageOptions
{
    /// <summary>Root folder; <c>public/</c> and <c>private/</c> live under it. Default: <c>{ContentRoot}/App_Data/storage</c>.</summary>
    public string? RootPath { get; set; }
}

/// <summary>Fills the environment-dependent defaults of <see cref="StorageOptions"/>.</summary>
public sealed class StorageOptionsDefaults(IHostEnvironment environment, IConfiguration configuration)
    : IPostConfigureOptions<StorageOptions>
{
    public void PostConfigure(string? name, StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Provider))
        {
            options.Provider = StorageOptions.IsLocalEnvironment(environment)
                ? StorageOptions.FileSystemProvider
                : StorageOptions.S3Provider;
        }

        if (!options.UsesFileSystem)
            return;

        if (string.IsNullOrWhiteSpace(options.FileSystem.RootPath))
            options.FileSystem.RootPath = Path.Combine(environment.ContentRootPath, "App_Data", "storage");

        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            && configuration["App:ApiBaseUrl"] is { Length: > 0 } apiBaseUrl)
        {
            options.PublicBaseUrl = $"{apiBaseUrl.TrimEnd('/')}{FileSystemFileStorage.PublicRequestPath}";
        }
    }
}

/// <summary>
/// Startup validation (<c>ValidateOnStart</c>): outside Development/Testing the app does not start
/// without a complete S3 configuration, so files can never silently land on the ephemeral container disk.
/// </summary>
public sealed class StorageOptionsValidator(IHostEnvironment environment) : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var failures = new List<string>();

        if (options.UsesFileSystem)
        {
            if (!StorageOptions.IsLocalEnvironment(environment))
            {
                failures.Add(
                    $"Storage:Provider=FileSystem is allowed only in Development and Testing (current environment: {environment.EnvironmentName}). " +
                    "Configure Supabase Storage (Storage:Provider=S3), see docs/runbooks/storage.md.");
            }

            if (!IsAbsoluteHttpUrl(options.PublicBaseUrl, requireHttps: false))
                failures.Add("Storage:PublicBaseUrl (or App:ApiBaseUrl) must be an absolute http(s) URL for the FileSystem provider.");
        }
        else if (options.UsesS3)
        {
            // External calls use HTTPS; plain http is tolerated only locally (e.g. a MinIO container).
            var requireHttps = !StorageOptions.IsLocalEnvironment(environment);
            var s3 = options.S3;
            if (!IsAbsoluteHttpUrl(s3.ServiceUrl, requireHttps))
                failures.Add("Storage:S3:ServiceUrl is required (absolute https URL of the S3 endpoint).");
            Require(failures, s3.Region, "Storage:S3:Region");
            Require(failures, s3.AccessKeyId, "Storage:S3:AccessKeyId");
            Require(failures, s3.SecretAccessKey, "Storage:S3:SecretAccessKey");
            Require(failures, s3.PublicBucket, "Storage:S3:PublicBucket");
            Require(failures, s3.PrivateBucket, "Storage:S3:PrivateBucket");
            if (!string.IsNullOrWhiteSpace(s3.PublicBucket)
                && string.Equals(s3.PublicBucket, s3.PrivateBucket, StringComparison.Ordinal))
            {
                failures.Add("Storage:S3:PublicBucket and Storage:S3:PrivateBucket must be two different buckets.");
            }

            if (!IsAbsoluteHttpUrl(options.PublicBaseUrl, requireHttps))
                failures.Add("Storage:PublicBaseUrl is required (absolute https public URL of the public bucket).");
        }
        else
        {
            failures.Add($"Storage:Provider '{options.Provider}' is not supported: use S3 or FileSystem.");
        }

        if (options.SignedUrlTtlMinutes is < 1 or > 60)
            failures.Add("Storage:SignedUrlTtlMinutes must be between 1 and 60.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"{key} is required.");
    }

    private static bool IsAbsoluteHttpUrl(string? value, bool requireHttps) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp));
}
