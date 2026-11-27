using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Casazen.Infrastructure.Http;

/// <summary>
/// Which URLs and IP addresses the server may contact on behalf of a user (anti-SSRF, A2-21 / A4-10 / A9-32).
/// </summary>
/// <remarks>
/// <see cref="TryParse"/> is the syntactic check done when a URL is saved and before each request or redirect:
/// https, an allowed port, no credentials, no internal host name and no literal IP of a blocked range. It does not
/// resolve DNS: the resolved address is checked by <see cref="SafeExternalHttpClient"/> on the socket it connects,
/// which also covers DNS rebinding. <see cref="IsBlockedAddress"/> accepts public unicast addresses only.
/// </remarks>
public static class ExternalUrlPolicy
{
    public const int MaxUrlLength = 2048;

    private static readonly string[] InternalHostSuffixes =
        [".localhost", ".local", ".internal", ".localdomain", ".arpa"];

    private static readonly IpRange[] BlockedIPv4Ranges =
    [
        IpRange.Parse("0.0.0.0/8"),
        IpRange.Parse("10.0.0.0/8"),
        IpRange.Parse("100.64.0.0/10"),
        IpRange.Parse("127.0.0.0/8"),
        IpRange.Parse("169.254.0.0/16"),
        IpRange.Parse("172.16.0.0/12"),
        IpRange.Parse("192.0.0.0/24"),
        IpRange.Parse("192.0.2.0/24"),
        IpRange.Parse("192.88.99.0/24"),
        IpRange.Parse("192.168.0.0/16"),
        IpRange.Parse("198.18.0.0/15"),
        IpRange.Parse("198.51.100.0/24"),
        IpRange.Parse("203.0.113.0/24"),
        IpRange.Parse("224.0.0.0/4"),
        IpRange.Parse("240.0.0.0/4"),
    ];

    private static readonly IpRange IPv6GlobalUnicast = IpRange.Parse("2000::/3");

    private static readonly IpRange[] BlockedIPv6GlobalRanges =
    [
        IpRange.Parse("2001::/23"),
        IpRange.Parse("2001:db8::/32"),
        IpRange.Parse("2002::/16"),
        IpRange.Parse("3fff::/20"),
    ];

    private static readonly IpRange Nat64 = IpRange.Parse("64:ff9b::/96");

    public static bool TryParse(string? url, IReadOnlyCollection<int> allowedPorts, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();
        if (trimmed.Length > MaxUrlLength || !Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate))
            return false;

        return TryValidate(candidate, allowedPorts, out uri);
    }

    public static bool TryValidate(Uri candidate, IReadOnlyCollection<int> allowedPorts, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (!candidate.IsAbsoluteUri
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !allowedPorts.Contains(candidate.Port))
        {
            return false;
        }

        switch (candidate.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                if (!IPAddress.TryParse(candidate.IdnHost, out var literal) || IsBlockedAddress(literal))
                    return false;
                break;
            case UriHostNameType.Dns:
                if (IsInternalHostName(candidate.IdnHost))
                    return false;
                break;
            default:
                return false;
        }

        uri = candidate;
        return true;
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return BlockedIPv4Ranges.Any(r => r.Contains(address));

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return true;

        if (address.IsIPv4MappedToIPv6)
            return true;

        if (Nat64.Contains(address))
        {
            var bytes = address.GetAddressBytes();
            return IsBlockedAddress(new IPAddress(bytes.AsSpan(12, 4)));
        }

        return !IPv6GlobalUnicast.Contains(address) || BlockedIPv6GlobalRanges.Any(r => r.Contains(address));
    }

    private static bool IsInternalHostName(string idnHost)
    {
        var host = idnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || !host.Contains('.'))
            return true;

        return host == "localhost" || InternalHostSuffixes.Any(host.EndsWith);
    }

    private readonly record struct IpRange(byte[] Network, int PrefixLength, AddressFamily Family)
    {
        public static IpRange Parse(string cidr)
        {
            var slash = cidr.IndexOf('/');
            var network = IPAddress.Parse(cidr[..slash]);
            return new IpRange(network.GetAddressBytes(), int.Parse(cidr[(slash + 1)..]), network.AddressFamily);
        }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != Family)
                return false;

            var bytes = address.GetAddressBytes();
            var fullBytes = PrefixLength / 8;
            for (var i = 0; i < fullBytes; i++)
            {
                if (bytes[i] != Network[i])
                    return false;
            }

            var remainingBits = PrefixLength % 8;
            if (remainingBits == 0)
                return true;

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (bytes[fullBytes] & mask) == (Network[fullBytes] & mask);
        }
    }
}
