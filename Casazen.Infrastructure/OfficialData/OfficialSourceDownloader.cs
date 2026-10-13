using System.Security.Cryptography;
using Casazen.Core.OfficialData;

namespace Casazen.Infrastructure.OfficialData;

/// <summary>Downloads a public official file. Hosts outside the allowlist are refused.</summary>
public class OfficialSourceDownloader(HttpClient httpClient)
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public async Task<OfficialDownload> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!OfficialHostAllowlist.IsAllowed(url))
            return OfficialDownload.Blocked(url);

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            return new OfficialDownload(url, status, null, null, $"HTTP {status}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                return new OfficialDownload(url, status, null, null, "file_too_large");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new OfficialDownload(url, status, bytes, sha, null);
    }
}

public sealed record OfficialDownload(Uri Url, int? HttpStatus, byte[]? Bytes, string? Sha256, string? Error)
{
    public bool Succeeded => Bytes is { Length: > 0 } && Error is null;

    public static OfficialDownload Blocked(Uri url) => new(url, null, null, null, "host_not_allowed");
}
