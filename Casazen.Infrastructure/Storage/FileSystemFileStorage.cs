using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Storage;

/// <summary>
/// <see cref="IFileStorage"/> on the local disk, for Development and tests ONLY (enforced by
/// <see cref="StorageOptionsValidator"/>). Layout: <c>{RootPath}/public/{key}</c> and
/// <c>{RootPath}/private/{key}</c>. The public folder is served by the API at
/// <see cref="PublicRequestPath"/>; the private folder is never served. It cannot sign URLs.
/// </summary>
public sealed class FileSystemFileStorage : IFileStorage
{
    /// <summary>Request path at which the API serves the public folder (Development/Testing only).</summary>
    public const string PublicRequestPath = "/storage/public";

    private readonly StorageOptions _options;
    private readonly ILogger<FileSystemFileStorage> _logger;

    public FileSystemFileStorage(IOptions<StorageOptions> options, ILogger<FileSystemFileStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
        RootPath = Path.GetFullPath(_options.FileSystem.RootPath
            ?? throw new InvalidOperationException("Storage:FileSystem:RootPath is not configured."));
    }

    public string RootPath { get; }

    public string BucketRoot(StorageBucket bucket) => Path.Combine(RootPath, bucket == StorageBucket.Public ? "public" : "private");

    public async Task PutAsync(
        StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(bucket, StorageKeys.EnsureValid(key));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            await content.CopyToAsync(file, cancellationToken);
        _logger.LogInformation("Stored object {Key} in local {Bucket} folder", key, bucket);
    }

    public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        if (!StorageKeys.IsValid(key))
            return Task.FromResult<Stream?>(null);

        var path = ResolvePath(bucket, key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(StorageKeys.IsValid(key) && File.Exists(ResolvePath(bucket, key)));

    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(bucket, StorageKeys.EnsureValid(key));
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted object {Key} from local {Bucket} folder", key, bucket);
        }

        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key) =>
        PublicUrls.Build(_options.PublicBaseUrl!, StorageKeys.EnsureValid(key));

    public string? TryGetPublicKey(string url) => PublicUrls.TryGetKey(_options.PublicBaseUrl!, url);

    /// <summary>The filesystem provider cannot sign URLs: callers fall back to the authenticated download.</summary>
    public Task<Uri?> GetSignedReadUrlAsync(
        string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);

    private string ResolvePath(StorageBucket bucket, string key)
    {
        var root = Path.GetFullPath(BucketRoot(bucket));
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        return path;
    }
}
