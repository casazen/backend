using System.Security.Cryptography;
using Casazen.Core.Options;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>What the check of one model file found: its full path when it can be used, otherwise a failure code.</summary>
/// <param name="Path">Full path of the verified file; null when it cannot be used.</param>
/// <param name="Failure">One of the <see cref="ModelFileProvisioner"/> codes; null when the file is verified.</param>
/// <param name="Sha256">The SHA-256 of the bytes that were checked (uppercase hexadecimal), when they were read.</param>
internal sealed record ModelFileCheck(string? Path, string? Failure, string? Sha256)
{
    public bool IsVerified => Failure is null;
}

/// <summary>
/// Brings a model file to disk and proves it is the pinned one (ADR-013-R8): the SHA-256 of the bytes must equal the configured hash. A file
/// is never used, and never downloaded, without a pinned hash; a download that does not match is deleted. The failure is a code, so the log
/// and the status of the store name the cause and never a value.
/// </summary>
internal static class ModelFileProvisioner
{
    public const string HashNotConfigured = "hash_not_configured";
    public const string FileMissing = "file_missing";
    public const string HashMismatch = "hash_mismatch";
    public const string DownloadFailed = "download_failed";
    public const string PathInvalid = "path_invalid";

    /// <summary>A model is far smaller than this; a stream that goes on is not the file it was announced as.</summary>
    internal const long MaxDownloadBytes = 1_500_000_000;

    public static async Task<ModelFileCheck> EnsureAsync(
        ModelFileOptions spec,
        string baseDirectory,
        Func<HttpClient>? httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!spec.HasWellFormedSha256())
            return new ModelFileCheck(null, HashNotConfigured, null);

        if (string.IsNullOrWhiteSpace(spec.Path))
            return new ModelFileCheck(null, PathInvalid, null);

        string path;
        try
        {
            path = System.IO.Path.IsPathRooted(spec.Path)
                ? spec.Path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, spec.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ModelFileCheck(null, PathInvalid, null);
        }

        var expected = spec.Sha256.Trim();
        if (!File.Exists(path))
        {
            if (string.IsNullOrWhiteSpace(spec.DownloadUrl) || httpClientFactory is null)
                return new ModelFileCheck(null, FileMissing, null);

            return await DownloadAsync(spec, path, expected, httpClientFactory, logger, cancellationToken);
        }

        try
        {
            var actual = await HashFileAsync(path, cancellationToken);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? new ModelFileCheck(path, null, actual)
                : new ModelFileCheck(null, HashMismatch, actual);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Model file {Model} could not be read ({ErrorType})", spec.Name, ex.GetType().Name);
            return new ModelFileCheck(null, FileMissing, null);
        }
    }

    /// <summary>The SHA-256 of a file, read in blocks (a model can be hundreds of megabytes).</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<ModelFileCheck> DownloadAsync(
        ModelFileOptions spec,
        string path,
        string expected,
        Func<HttpClient> httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var partial = path + ".partial";
        try
        {
            logger.LogInformation("Downloading biometric model {Model} (SHA-256 is verified before use)", spec.Name);
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var client = httpClientFactory();
            using var response = await client.GetAsync(spec.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                return new ModelFileCheck(null, DownloadFailed, null);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await network.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                        return new ModelFileCheck(null, DownloadFailed, null);

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return new ModelFileCheck(null, HashMismatch, actual);

            File.Move(partial, path, overwrite: true);
            return new ModelFileCheck(path, null, actual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.LogWarning("Download of biometric model {Model} failed ({ErrorType})", spec.Name, ex.GetType().Name);
            return new ModelFileCheck(null, DownloadFailed, null);
        }
        finally
        {
            try
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover partial file is not used by anything: the next start overwrites it.
            }
        }
    }
}
