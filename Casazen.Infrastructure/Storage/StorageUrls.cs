using System.Net.Http.Headers;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Storage;

/// <summary>Public URL mapping shared by the storage providers: <c>{publicBaseUrl}/{key}</c>.</summary>
internal static class PublicUrls
{
    public static string Build(string publicBaseUrl, string key) =>
        $"{publicBaseUrl.TrimEnd('/')}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";

    public static string? TryGetKey(string publicBaseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var prefix = publicBaseUrl.TrimEnd('/') + "/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var key = Uri.UnescapeDataString(url[prefix.Length..].Split('?', '#')[0]);
        return StorageKeys.IsValid(key) ? key : null;
    }
}

internal static class ContentDisposition
{
    /// <summary><c>attachment</c> header value with an ASCII fallback and the RFC 5987 UTF-8 file name.</summary>
    public static string Attachment(string fileName)
    {
        var header = new ContentDispositionHeaderValue("attachment")
        {
            FileName = $"\"{AsciiFallback(fileName)}\"",
            FileNameStar = fileName,
        };
        return header.ToString();
    }

    private static string AsciiFallback(string fileName)
    {
        var chars = fileName.Select(c => c is >= ' ' and <= '~' and not '"' and not '\\' ? c : '_').ToArray();
        return new string(chars);
    }
}
