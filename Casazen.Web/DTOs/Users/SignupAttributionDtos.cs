using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs.Users;

/// <summary>
/// Body of <c>POST /api/users/me/signup-attribution</c> (SE-03): the values the web app captured on <c>/signup</c>.
/// Every field is optional; a value that breaks <see cref="SignupAttributionRules"/> is refused (400, or 422 for an unknown
/// comune), never truncated.
/// </summary>
public sealed class SignupAttributionRequestDto
{
    [Display(Name = "utm_source")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmSource { get; set; }

    [Display(Name = "utm_medium")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmMedium { get; set; }

    [Display(Name = "utm_campaign")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmCampaign { get; set; }

    [Display(Name = "utm_term")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmTerm { get; set; }

    [Display(Name = "utm_content")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmContent { get; set; }

    /// <summary>
    /// Slug of the comune of the SEO page (e.g. <c>como</c>) or its ISTAT code; one CasaZen does not know is refused by
    /// the service (422 <c>signup_attribution_unknown_comune</c>).
    /// </summary>
    [Display(Name = "comune")]
    [StringLength(SignupAttributionRules.MaxComuneLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.ComunePattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? Comune { get; set; }

    /// <summary>Path of the first page of the visit, without query string (e.g. <c>/p/affitti-brevi/lombardia/como</c>).</summary>
    [Display(Name = "landingPath")]
    [StringLength(SignupAttributionRules.MaxLandingPathLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.LandingPathPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? LandingPath { get; set; }

    /// <summary>Host of the referring site (e.g. <c>www.google.com</c>), never the full URL.</summary>
    [Display(Name = "referrerHost")]
    [StringLength(SignupAttributionRules.MaxReferrerHostLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.ReferrerHostPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? ReferrerHost { get; set; }

    public SignupAttributionInput ToInput() =>
        new(UtmSource, UtmMedium, UtmCampaign, UtmTerm, UtmContent, Comune, LandingPath, ReferrerHost);
}

/// <summary>Answer of <c>POST /api/users/me/signup-attribution</c>.</summary>
public sealed class SignupAttributionResultDto
{
    /// <summary>
    /// <c>true</c> when this call stored the attribution, <c>false</c> when the org already had one (the first one is
    /// kept). Either way the client can forget the values it kept.
    /// </summary>
    public bool Recorded { get; set; }
}

/// <summary>An attribution in the admin report (<c>GET /api/admin/signup-attributions</c>).</summary>
public sealed class SignupAttributionAdminDto
{
    public Guid OrgId { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmTerm { get; set; }
    public string? UtmContent { get; set; }

    /// <summary>ISTAT code of the comune.</summary>
    public string? ComuneCode { get; set; }

    public string? ComuneName { get; set; }
    public string? LandingPath { get; set; }
    public string? ReferrerHost { get; set; }

    public static SignupAttributionAdminDto From(SignupAttributionRecord record) => new()
    {
        OrgId = record.OrgId,
        RecordedAt = record.RecordedAt,
        UtmSource = record.UtmSource,
        UtmMedium = record.UtmMedium,
        UtmCampaign = record.UtmCampaign,
        UtmTerm = record.UtmTerm,
        UtmContent = record.UtmContent,
        ComuneCode = record.ComuneCode,
        ComuneName = record.ComuneName,
        LandingPath = record.LandingPath,
        ReferrerHost = record.ReferrerHost,
    };
}
