using System.Net;
using System.Net.Sockets;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>An attachment of an OTA message as the proxy hands it to the client: the bytes and the type the answer of the channel declared.</summary>
/// <param name="Content">The file, whole (at most <see cref="OtaAttachmentRules.MaxBytes"/>).</param>
/// <param name="ContentType">One of <see cref="OtaAttachmentRules.AllowedContentTypes"/>.</param>
public sealed record OtaAttachmentContent(byte[] Content, string ContentType)
{
    /// <summary>True for a type the browser must not render: it is offered as a download.</summary>
    public bool AsDownload => OtaAttachmentRules.IsDownloadOnly(ContentType);
}

/// <summary>
/// Reads the file an OTA message points at (E05/F02/US01/T01, ADR-032-R5). The address was given by the channel manager and is stored with the
/// message; it never leaves the server, so this is the only place that uses it.
/// </summary>
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
    /// <summary>The most a file may weigh: 20 MB.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>How long the whole fetch may take: 30 seconds.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string OctetStream = "application/octet-stream";

    /// <summary>The types served: two images, and any other file as a download.</summary>
    public static IReadOnlyList<string> AllowedContentTypes { get; } = [Jpeg, Png, OctetStream];

    /// <summary>
    /// The type of a <c>Content-Type</c> header without its parameters, in lower case, when it is one of <see cref="AllowedContentTypes"/>;
    /// <c>null</c> for anything else (an HTML page, an SVG, a missing header).
    /// </summary>
    public static string? AllowedContentTypeOf(string? mediaType)
    {
        var type = mediaType?.Split(';')[0].Trim().ToLowerInvariant();
        return type is not null && AllowedContentTypes.Contains(type, StringComparer.Ordinal) ? type : null;
    }

    /// <summary>True for the types a browser must not render from our origin: they are offered as a download, whatever they claim to be.</summary>
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

    /// <summary>
    /// True for an address on the public internet: not loopback, not unspecified, not private, link-local (the metadata service of a cloud sits
    /// at 169.254.169.254), shared (carrier-grade NAT), unique-local, multicast or reserved. An IPv4 address carried in an IPv6 one is judged as
    /// the IPv4 address it carries.
    /// </summary>
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
