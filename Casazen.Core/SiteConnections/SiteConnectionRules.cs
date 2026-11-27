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
    /// <summary>How long a code opens: five minutes.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a code is kept after it expired: a day. The older ones are deleted when a new code is created.</summary>
    public static readonly TimeSpan ExpiredCodeRetention = TimeSpan.FromDays(1);

    /// <summary>The resolution of <c>SiteConnection.LastUsedAt</c>: written again at most once every five minutes.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    /// <summary>Longest site address (an origin is far shorter): the column length.</summary>
    public const int SiteUrlMaxLength = 300;

    /// <summary>Longest return address.</summary>
    public const int ReturnUrlMaxLength = 2048;

    /// <summary>Longest id of an account (the Auth0 subject).</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Characters of a PKCE challenge <c>S256</c>: 32 bytes in base64url.</summary>
    public const int CodeChallengeLength = 43;

    /// <summary>The only PKCE method accepted (RFC 7636): <c>plain</c> is refused.</summary>
    public const string CodeChallengeMethod = "S256";

    /// <summary>The query parameter of the code in the address WordPress is sent back to.</summary>
    public const string CodeQueryParameter = "casazen_code";

    /// <summary>The query parameter of the state of the plugin in the same address.</summary>
    public const string StateQueryParameter = "state";

    /// <summary>The site address normalized to its origin (<c>https://www.example.com</c>), or false for anything that is not an origin allowed here.</summary>
    public static bool TryNormalizeSiteUrl(string? value, [NotNullWhen(true)] out string? origin)
    {
        origin = null;
        if (string.IsNullOrEmpty(value) || value.Length > SiteUrlMaxLength || HasUnsafeCharacters(value))
            return false;

        // An origin and nothing else: no credentials, no query, no fragment, and after the authority at most the root «/».
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || value.IndexOfAny(['@', '?', '#']) >= 0)
            return false;

        var afterScheme = value.AsSpan(schemeEnd + 3);
        var slash = afterScheme.IndexOf('/');
        if (slash >= 0 && slash != afterScheme.Length - 1)
            return false;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && TryGetOrigin(uri, out origin);
    }

    /// <summary>
    /// The return address of a request, when it is allowed for the site <paramref name="siteOrigin"/> (already normalized): the same origin, no credentials,
    /// no fragment, not longer than <see cref="ReturnUrlMaxLength"/>, without the parameters CasaZen adds.
    /// </summary>
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

        // The code and the state are added by CasaZen alone: an address that carries either already is refused, never left ambiguous.
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.ContainsKey(CodeQueryParameter) || query.ContainsKey(StateQueryParameter))
            return false;

        returnUri = uri;
        return true;
    }

    /// <summary>The state of the plugin: 1 to 128 characters of <c>[A-Za-z0-9_-]</c>.</summary>
    public static bool IsValidState([NotNullWhen(true)] string? value) => value is not null && StatePattern().IsMatch(value);

    /// <summary>A PKCE challenge <c>S256</c>: 43 characters of base64url.</summary>
    public static bool IsValidCodeChallenge([NotNullWhen(true)] string? value) => value is not null && ChallengePattern().IsMatch(value);

    /// <summary>True for <c>S256</c> exactly (RFC 7636 names are case-sensitive).</summary>
    public static bool IsS256(string? method) => string.Equals(method, CodeChallengeMethod, StringComparison.Ordinal);

    /// <summary>A PKCE verifier (RFC 7636 §4.1): 43 to 128 characters of <c>[A-Za-z0-9-._~]</c>.</summary>
    public static bool IsValidCodeVerifier([NotNullWhen(true)] string? value) => value is not null && VerifierPattern().IsMatch(value);

    /// <summary>
    /// The address WordPress is sent back to: <paramref name="returnUri"/> with <see cref="CodeQueryParameter"/> and <see cref="StateQueryParameter"/> added by
    /// the query builder of ASP.NET Core (encoded, after the query the address already has).
    /// </summary>
    public static string RedirectUrl(Uri returnUri, string code, string state)
    {
        ArgumentNullException.ThrowIfNull(returnUri);
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(state);

        KeyValuePair<string, string?>[] parameters = [new(CodeQueryParameter, code), new(StateQueryParameter, state)];
        return QueryHelpers.AddQueryString(returnUri.AbsoluteUri, parameters);
    }

    /// <summary>The host of a normalized site origin, the only part of a site address that goes in a log.</summary>
    public static string HostOf(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>A development host, the only kind allowed on <c>http</c>: <c>localhost</c>, <c>127.0.0.1</c>, <c>*.localhost</c>, <c>*.test</c>.</summary>
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

    /// <summary>Control characters, white space and backslashes: never in an address CasaZen accepts.</summary>
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
