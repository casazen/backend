using System.Net.Http.Headers;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Storage;

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
