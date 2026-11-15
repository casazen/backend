using System.Text.RegularExpressions;

namespace Casazen.Core.Validation;

/// <summary>
/// What the signup attribution may contain (SE-03, A8-03): the UTM parameters of the visit, the comune of the SEO page,
/// the path of the landing page and the host of the referrer. Marketing values only, never personal data: an email
/// address cannot pass (no <c>@</c>), the path carries no query string or fragment, the referrer is reduced to its host.
/// </summary>
/// <remarks>
/// A value that breaks a rule is <b>refused</b>, never truncated, and nothing is stored: 400 <c>validation_error</c> for
/// a length or a character outside the rule, 422 <c>signup_attribution_unknown_comune</c> for a well-formed comune that
/// CasaZen does not know. The web app applies the same rules when it captures the values
/// (<c>src/lib/signup-attribution.ts</c>) and drops the invalid ones, so a stray parameter in a link does not cost the
/// rest of the attribution.
/// </remarks>
public static class SignupAttributionRules
{
    public const int MaxUtmLength = 100;
    public const int MaxComuneLength = 100;
    public const int MaxLandingPathLength = 200;
    public const int MaxReferrerHostLength = 253;

    /// <summary>ISTAT code of a comune: 6 digits.</summary>
    public const int ComuneCodeLength = 6;

    /// <summary>Letters of any alphabet, digits, space and <c>- _ . ~ + | : , / ( ) !</c>.</summary>
    public const string UtmPattern = @"^[\p{L}\p{N} ._~+|:,/()!-]+$";

    /// <summary>Slug of a comune (<c>como</c>, <c>reggio-emilia</c>) or its 6-digit ISTAT code.</summary>
    public const string ComunePattern = @"^([0-9]{6}|[a-z0-9]+(-[a-z0-9]+)*)$";

    /// <summary>A path of the web app: starts with <c>/</c>; no query, fragment, percent-encoding or <c>@</c>.</summary>
    public const string LandingPathPattern = @"^/[A-Za-z0-9/._~-]*$";

    /// <summary>A host name in lower case: no scheme, port, path or credentials.</summary>
    public const string ReferrerHostPattern = @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$";

    /// <summary>Resource key (SharedResources) of a value longer than allowed.</summary>
    public const string TooLongKey = "SignupAttributionTooLong";

    /// <summary>Resource key of a value with characters outside the rule.</summary>
    public const string InvalidCharactersKey = "SignupAttributionInvalidCharacters";

    /// <summary>Resource key of a comune that CasaZen does not know.</summary>
    public const string UnknownComuneKey = "SignupAttributionUnknownComune";

    /// <summary>Trimmed value, <c>null</c> when empty.</summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The longest text of a value that is looked at at all: a longer one cannot be a token nor an address of a site, so it is never parsed.</summary>
    public const int MaxRawValueLength = 2048;

    /// <summary>The longest run of digits a token may hold: more is a phone number or an identity number, which is personal data and not a marketing value.</summary>
    public const int MaxDigitRunInToken = 8;

    /// <summary>
    /// A marketing value that is a lower-case token (E27/F01/US01/T01, NF-123): the source, the medium or the campaign of the link a guest booked from.
    /// Trimmed and put in lower case, it is kept only when it is a token: letters and digits of any alphabet, and <c>-</c>, <c>_</c> and <c>.</c> inside
    /// (never first or last), so a space, an <c>@</c> (an e-mail address), a <c>:</c>, a <c>/</c> or a <c>?</c> (an address of a page, a query) never
    /// pass; it has at most <paramref name="maxLength"/> characters and no run of more than <see cref="MaxDigitRunInToken"/> digits (a phone number).
    /// Unlike the signup attribution a value that breaks a rule is <b>dropped</b> (<c>null</c>), never truncated and never refused: a stray parameter
    /// of a link must not cost the guest the booking.
    /// </summary>
    /// <param name="value">What the link carried.</param>
    /// <param name="maxLength">The longest token kept: the size of the column.</param>
    public static string? SanitizeToken(string? value, int maxLength)
    {
        if (value is null || value.Length > MaxRawValueLength)
            return null;

        var token = Normalize(value)?.ToLowerInvariant();
        if (token is null || token.Length > maxLength || !IsToken(token))
            return null;

        return HasDigitRunLongerThan(token, MaxDigitRunInToken) ? null : token;
    }

    /// <summary>
    /// The host name of the site a visitor came from (E27/F01/US01/T01, NF-123): <paramref name="value"/> is an address (<c>https://www.agenzia.it/case?x=1</c>)
    /// or a host with or without a path, and only its host name is kept, in lower case and in ASCII (an internationalized name in its punycode form): never
    /// the scheme, the credentials, the port, the path, the query or the fragment. <c>null</c> when it is not an address of a site on the internet: a
    /// scheme other than http or https, an IP address, a name with no dot (a machine of a private network may be named after a person), a name that is
    /// not a host name (<see cref="ReferrerHostPattern"/>) or longer than <see cref="MaxReferrerHostLength"/>.
    /// </summary>
    /// <param name="value">The referrer as the browser gave it, or the host alone.</param>
    public static string? ReferrerHostOf(string? value)
    {
        if (value is null || value.Length > MaxRawValueLength)
            return null;

        var text = Normalize(value);
        if (text is null)
            return null;

        // A bare host (<c>www.agenzia.it/case</c>) is read as an address on https; any other scheme is an address that is not a page of a site.
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return null;
        }

        string host;
        try
        {
            host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UriFormatException)
        {
            return null;
        }

        return host.Contains('.', StringComparison.Ordinal)
               && host.Length <= MaxReferrerHostLength
               && Regex.IsMatch(host, ReferrerHostPattern, RegexOptions.CultureInvariant)
            ? host
            : null;
    }

    private static bool IsToken(string token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (char.IsLetterOrDigit(c))
                continue;

            var inside = i > 0 && i < token.Length - 1;
            if (!inside || (c != '-' && c != '_' && c != '.'))
                return false;
        }

        return token.Length > 0;
    }

    private static bool HasDigitRunLongerThan(string token, int limit)
    {
        var run = 0;
        foreach (var c in token)
        {
            run = char.IsDigit(c) ? run + 1 : 0;
            if (run > limit)
                return true;
        }

        return false;
    }
}
