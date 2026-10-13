using System.Text.RegularExpressions;

namespace Casazen.Infrastructure.Email;

/// <summary>Normalization of the host names the public site receives (Host header, <c>host</c> query parameters).</summary>
public static partial class PublicSiteHosts
{
    /// <summary>
    /// A host name in lower case, without port and trailing dot; <c>null</c> when <paramref name="value"/> is empty or
    /// is not a plain DNS name (letters, digits, hyphens and dots, at most 253 characters). A value that is not a DNS
    /// name is never used: it is not echoed anywhere and never matches a host (BK-15, host header injection).
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var host = value.Trim().ToLowerInvariant();
        var colon = host.LastIndexOf(':');
        if (colon >= 0 && host.IndexOf(']') < 0 && host[(colon + 1)..].All(char.IsAsciiDigit))
            host = host[..colon];

        host = host.TrimEnd('.');
        return host.Length is > 0 and <= 253 && DnsName().IsMatch(host) ? host : null;
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex DnsName();
}
