using System.Net;
using System.Net.Sockets;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public sealed record OtaAttachmentContent(byte[] Content, string ContentType)
{
    public bool AsDownload => OtaAttachmentRules.IsDownloadOnly(ContentType);
}

public interface IOtaAttachmentFetcher
{
    /// <summary>
    /// The file at <paramref name="address"/>, or <c>null</c> when it cannot be served: the address is gone or expired (a client error or a redirect),
    /// is not an https address of a public host, does not answer in time, is larger than <see cref="OtaAttachmentRules.MaxBytes"/>, or is of a type that
    /// is not allowed. Never throws for any of those, and never logs the address.
    /// </summary>
    Task<OtaAttachmentContent?> FetchAsync(string address, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules of the attachment proxy (E05/F02/US01/T01, ADR-032-R5), pure: what address is fetched, from which hosts, how much and of which type.
/// The address comes from a third party (the channel manager reads it from the channel), so it is treated as untrusted input: only an https
/// address of a public host on the default port is fetched, a redirect is never followed, and what comes back is accepted only if it is small
/// enough and of one of the three types the hub shows.
/// </summary>
public static class OtaAttachmentRules
{
    public const int MaxBytes = 20 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string OctetStream = "application/octet-stream";

    public static IReadOnlyList<string> AllowedContentTypes { get; } = [Jpeg, Png, OctetStream];

    public static string? AllowedContentTypeOf(string? mediaType)
    {
        var type = mediaType?.Split(';')[0].Trim().ToLowerInvariant();
        return type is not null && AllowedContentTypes.Contains(type, StringComparer.Ordinal) ? type : null;
    }

    public static bool IsDownloadOnly(string contentType) =>
        !string.Equals(contentType, Jpeg, StringComparison.Ordinal) && !string.Equals(contentType, Png, StringComparison.Ordinal);

    /// <summary>
    /// True for an address that may be fetched: absolute, <c>https</c>, on the default port, with a host name (not an IP address, not
    /// <c>localhost</c>, not an internal name) and no user or password in it. The host is checked again when the connection is made
    /// (<see cref="IsPublicAddress"/>), because a name can point anywhere.
    /// </summary>
    public static bool TryGetFetchUri(string? address, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(address) || address.Length > OtaMessage.AttachmentUrlMaxLength)
            return false;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.IsDefaultPort
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || parsed.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = parsed.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0
            || !host.Contains('.', StringComparison.Ordinal)
            || host is "localhost"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".intranet", StringComparison.Ordinal)
            || host.EndsWith(".lan", StringComparison.Ordinal)
            || host.EndsWith(".home", StringComparison.Ordinal))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
            return false;

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                var b = address.GetAddressBytes();
                return !(b[0] == 0
                    || b[0] == 10
                    || b[0] == 127
                    || (b[0] == 100 && b[1] is >= 64 and <= 127)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && b[1] is >= 16 and <= 31)
                    || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 198 && b[1] is 18 or 19)
                    || b[0] >= 224);
            case AddressFamily.InterNetworkV6:
                return !(address.IsIPv6LinkLocal
                    || address.IsIPv6SiteLocal
                    || address.IsIPv6Multicast
                    || address.IsIPv6UniqueLocal
                    || address.IsIPv6Teredo);
            default:
                return false;
        }
    }
}
