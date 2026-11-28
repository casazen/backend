using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Casazen.Core.SiteConnections;

/// <summary>
/// The rules of the connection of a site to the org (S022, PLG-BE-CONNECT, site integration plan §10.3-§10.4): what a site address, a return address, a
/// state and a PKCE challenge must be, how long a code lives, and how the address WordPress is sent back to is built. Any violation of a request of
/// authorization is the one answer 400 <see cref="SiteConnectionErrorCodes.InvalidRequest"/>.
/// </summary>
/// <remarks>
/// <para><b>The site</b> is an origin only (scheme, host and port): <c>https</c>, or <c>http</c> for a development host (<c>localhost</c>, <c>127.0.0.1</c>,
/// <c>*.localhost</c>, <c>*.test</c>); no credentials, path, query or fragment (a single trailing <c>/</c> is the root itself and is dropped). The host is
/// a DNS name or an IPv4 address, stored lowercase in its ASCII form, the default port left out.</para>
/// <para><b>The return address</b> has the same origin as the site, no credentials, no fragment, at most <see cref="ReturnUrlMaxLength"/> characters, and does
/// not carry <see cref="CodeQueryParameter"/> or <see cref="StateQueryParameter"/> already: CasaZen adds them with a URL builder, keeping the query it has.</para>
/// </remarks>
public static partial class SiteConnectionRules
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ExpiredCodeRetention = TimeSpan.FromDays(1);

    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    public const int SiteUrlMaxLength = 300;

    public const int ReturnUrlMaxLength = 2048;

    public const int UserIdMaxLength = 255;

    public const int CodeChallengeLength = 43;

    public const string CodeChallengeMethod = "S256";

    public const string CodeQueryParameter = "casazen_code";

    public const string StateQueryParameter = "state";

    public static bool TryNormalizeSiteUrl(string? value, [NotNullWhen(true)] out string? origin)
    {
        origin = null;
        if (string.IsNullOrEmpty(value) || value.Length > SiteUrlMaxLength || HasUnsafeCharacters(value))
            return false;

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || value.IndexOfAny(['@', '?', '#']) >= 0)
            return false;

        var afterScheme = value.AsSpan(schemeEnd + 3);
        var slash = afterScheme.IndexOf('/');
        if (slash >= 0 && slash != afterScheme.Length - 1)
            return false;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && TryGetOrigin(uri, out origin);
    }

    public static bool TryParseReturnUrl(string? value, string siteOrigin, [NotNullWhen(true)] out Uri? returnUri)
    {
        ArgumentNullException.ThrowIfNull(siteOrigin);
        returnUri = null;
        if (string.IsNullOrEmpty(value) || value.Length > ReturnUrlMaxLength || HasUnsafeCharacters(value) || value.Contains('#'))
            return false;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !TryGetOrigin(uri, out var origin)
            || !string.Equals(origin, siteOrigin, StringComparison.Ordinal))
        {
            return false;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.ContainsKey(CodeQueryParameter) || query.ContainsKey(StateQueryParameter))
            return false;

        returnUri = uri;
        return true;
    }

    public static bool IsValidState([NotNullWhen(true)] string? value) => value is not null && StatePattern().IsMatch(value);

    public static bool IsValidCodeChallenge([NotNullWhen(true)] string? value) => value is not null && ChallengePattern().IsMatch(value);

    public static bool IsS256(string? method) => string.Equals(method, CodeChallengeMethod, StringComparison.Ordinal);

    public static bool IsValidCodeVerifier([NotNullWhen(true)] string? value) => value is not null && VerifierPattern().IsMatch(value);

    public static string RedirectUrl(Uri returnUri, string code, string state)
    {
        ArgumentNullException.ThrowIfNull(returnUri);
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(state);

        KeyValuePair<string, string?>[] parameters = [new(CodeQueryParameter, code), new(StateQueryParameter, state)];
        return QueryHelpers.AddQueryString(returnUri.AbsoluteUri, parameters);
    }

    public static string HostOf(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    public static bool IsDevelopmentHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host is "localhost" or "127.0.0.1"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".test", StringComparison.Ordinal);
    }

    private static bool TryGetOrigin(Uri uri, [NotNullWhen(true)] out string? origin)
    {
        origin = null;
        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        var http = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
        if ((!https && !http)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (host.Length is 0 or > 253 || (http && !IsDevelopmentHost(host)))
            return false;

        origin = uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    private static bool HasUnsafeCharacters(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StatePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._~-]{43,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VerifierPattern();
}
