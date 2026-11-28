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
    public const string UnknownKey = "unknown";

    public static IPAddress? GetAddress(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    public static string? GetString(HttpContext httpContext) => GetAddress(httpContext)?.ToString();

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
