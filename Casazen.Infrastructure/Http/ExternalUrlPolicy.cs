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

    // IPv4 ranges that are not globally reachable (RFC 6890 and updates).
    private static readonly IpRange[] BlockedIPv4Ranges =
    [
        IpRange.Parse("0.0.0.0/8"),        // "this" network
        IpRange.Parse("10.0.0.0/8"),       // private
        IpRange.Parse("100.64.0.0/10"),    // carrier-grade NAT
        IpRange.Parse("127.0.0.0/8"),      // loopback
        IpRange.Parse("169.254.0.0/16"),   // link-local, cloud metadata (169.254.169.254)
        IpRange.Parse("172.16.0.0/12"),    // private
        IpRange.Parse("192.0.0.0/24"),     // IETF protocol assignments
        IpRange.Parse("192.0.2.0/24"),     // documentation
        IpRange.Parse("192.88.99.0/24"),   // 6to4 relay anycast
        IpRange.Parse("192.168.0.0/16"),   // private
        IpRange.Parse("198.18.0.0/15"),    // benchmarking
        IpRange.Parse("198.51.100.0/24"),  // documentation
        IpRange.Parse("203.0.113.0/24"),   // documentation
        IpRange.Parse("224.0.0.0/4"),      // multicast
        IpRange.Parse("240.0.0.0/4"),      // reserved, broadcast
    ];

    // Only IPv6 global unicast (2000::/3) is reachable: loopback, unspecified, IPv4-mapped/compatible,
    // ULA (fc00::/7), link-local (fe80::/10), site-local and multicast (ff00::/8) all fall outside it.
    private static readonly IpRange IPv6GlobalUnicast = IpRange.Parse("2000::/3");

    private static readonly IpRange[] BlockedIPv6GlobalRanges =
    [
        IpRange.Parse("2001::/23"),        // IETF protocol assignments (Teredo, ORCHID, benchmarking)
        IpRange.Parse("2001:db8::/32"),    // documentation
        IpRange.Parse("2002::/16"),        // 6to4 (embeds an IPv4 address)
        IpRange.Parse("3fff::/20"),        // documentation
    ];

    // Well-known NAT64 prefix: the embedded IPv4 address decides.
    private static readonly IpRange Nat64 = IpRange.Parse("64:ff9b::/96");

    /// <summary>
    /// True when <paramref name="url"/> may be fetched: absolute https URL, port in <paramref name="allowedPorts"/>,
    /// no user info, a public host name or a literal IP that <see cref="IsBlockedAddress"/> accepts.
    /// </summary>
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

    /// <summary>Same checks as <see cref="TryParse"/> for an already parsed URI (e.g. a redirect target).</summary>
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

    /// <summary>
    /// True for every address the server must not contact: private, loopback, link-local (cloud metadata),
    /// CGNAT, multicast, reserved and documentation ranges, IPv6 ULA / link-local and IPv4-mapped addresses.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return BlockedIPv4Ranges.Any(r => r.Contains(address));

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return true;

        // IPv4-mapped (::ffff:a.b.c.d) never comes from a public AAAA record: always refused.
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
            return true; // single-label names (e.g. "localhost", "postgres") only resolve inside a network

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
