using System.Net;
using System.Net.Sockets;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The client IP address of the current request, as resolved by <c>UseForwardedHeaders</c> (the first middleware, see
/// <see cref="Extensions.ForwardedHeadersServiceCollectionExtensions"/>): the address the trusted proxy appended to
/// <c>X-Forwarded-For</c>, or the TCP peer when there is no trusted proxy in front of the app.
/// </summary>
/// <remarks>
/// Never read <c>X-Forwarded-For</c> directly: its leftmost values are written by the client and can be anything
/// (A1-12, A9-10). The value is used as GDPR consent evidence and as the rate limiting partition key.
/// </remarks>
public static class ClientIp
{
    /// <summary>Partition key of requests whose client address is unknown (e.g. the in-memory test server).</summary>
    public const string UnknownKey = "unknown";

    /// <summary>
    /// The client address, with an IPv4 address mapped to IPv6 (<c>::ffff:1.2.3.4</c>, as Kestrel reports it on a
    /// dual-stack socket) returned as plain IPv4. Null when the server does not know the peer.
    /// </summary>
    public static IPAddress? GetAddress(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>The client address as text (consent evidence), or null when unknown.</summary>
    public static string? GetString(HttpContext httpContext) => GetAddress(httpContext)?.ToString();

    /// <summary>
    /// Rate limiting partition of the client: the IPv4 address, or the /64 prefix of an IPv6 address (a single
    /// subscriber usually owns a whole /64 and can rotate the lower 64 bits at will).
    /// </summary>
    public static string GetRateLimitKey(HttpContext httpContext)
    {
        var address = GetAddress(httpContext);
        if (address is null)
            return UnknownKey;

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
