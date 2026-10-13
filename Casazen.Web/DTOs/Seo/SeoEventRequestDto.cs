using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs.Seo;

/// <summary>
/// Body of <c>POST /api/public/seo/events</c> (SE-04, #300 AC3): one event of the SEO funnel, sent by the web app with a
/// beacon. No personal data is accepted or stored: the marketing values follow <see cref="SignupAttributionRules"/>
/// (no <c>@</c>, no URL, the referrer is a host); a value outside the rules is refused (400), never truncated.
/// </summary>
public sealed class SeoEventRequestDto
{
    /// <summary><c>cta_click</c> or <c>signup_start</c>; another name is refused by the service (422 <c>seo_event_unknown</c>).</summary>
    [Required(ErrorMessage = "SeoEventUnknown")]
    [StringLength(20, ErrorMessage = SignupAttributionRules.TooLongKey)]
    public string Event { get; set; } = string.Empty;

    /// <summary>Slug of the comune of the SEO page (<c>como</c>) or its ISTAT code; an unknown one is refused (422).</summary>
    [Display(Name = "comuneSlug")]
    [Required(ErrorMessage = SignupAttributionRules.UnknownComuneKey)]
    [StringLength(SignupAttributionRules.MaxComuneLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.ComunePattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string ComuneSlug { get; set; } = string.Empty;

    [Display(Name = "utmSource")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmSource { get; set; }

    [Display(Name = "utmMedium")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmMedium { get; set; }

    [Display(Name = "utmCampaign")]
    [StringLength(SignupAttributionRules.MaxUtmLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.UtmPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? UtmCampaign { get; set; }

    /// <summary>Host of the referring site (<c>www.google.com</c>), never the full URL.</summary>
    [Display(Name = "referrerHost")]
    [StringLength(SignupAttributionRules.MaxReferrerHostLength, ErrorMessage = SignupAttributionRules.TooLongKey)]
    [RegularExpression(SignupAttributionRules.ReferrerHostPattern, ErrorMessage = SignupAttributionRules.InvalidCharactersKey)]
    public string? ReferrerHost { get; set; }

    public SeoEventInput ToInput() => new(Event, ComuneSlug, UtmSource, UtmMedium, UtmCampaign, ReferrerHost);
}
