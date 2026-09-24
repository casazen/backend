using Casazen.Core.Regulatory;

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

    /// <summary>The comune of a slug or ISTAT code, <c>null</c> when CasaZen does not know it.</summary>
    public static ComuneInfo? ResolveComune(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return ItalianComuneRegistry.GetBySlug(trimmed) ?? ItalianComuneRegistry.GetByCode(trimmed);
    }

    /// <summary>Trimmed value, <c>null</c> when empty.</summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
