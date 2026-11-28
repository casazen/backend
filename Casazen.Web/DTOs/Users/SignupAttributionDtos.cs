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

    [Display(Name = "landingPath")]
    [StringLength(SignupAttributionRules.MaxLandingPathLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.LandingPathPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? LandingPath { get; set; }

    [Display(Name = "referrerHost")]
    [StringLength(SignupAttributionRules.MaxReferrerHostLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.ReferrerHostPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? ReferrerHost { get; set; }

    public SignupAttributionInput ToInput() =>
        new(UtmSource, UtmMedium, UtmCampaign, UtmTerm, UtmContent, Comune, LandingPath, ReferrerHost);
}

public sealed class SignupAttributionResultDto
{
    public bool Recorded { get; set; }
}

public sealed class SignupAttributionAdminDto
{
    public Guid OrgId { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmTerm { get; set; }
    public string? UtmContent { get; set; }

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
