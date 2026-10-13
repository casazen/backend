using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Casazen.Core.Validation;

namespace Casazen.Core.Entities;

/// <summary>
/// Where the signup of an org came from (SE-03, A8-03): the UTM parameters, the comune of the SEO page, the landing path
/// and the referrer host captured by the web app on <c>/signup</c>, recorded after the first onboarding. At most one per
/// org (unique <see cref="OrgId"/>): the first attribution is kept, later ones are ignored.
/// </summary>
/// <remarks>No personal data: only the values allowed by <see cref="SignupAttributionRules"/>.</remarks>
[Table("SignupAttributions")]
public class SignupAttribution : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmSource { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmMedium { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmCampaign { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmTerm { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmContent { get; set; }

    /// <summary>ISTAT code of the comune (the web app sends the slug of the SEO page, resolved on the registry).</summary>
    [MaxLength(SignupAttributionRules.ComuneCodeLength)]
    public string? ComuneCode { get; set; }

    /// <summary>Path of the first page of the visit (e.g. <c>/p/affitti-brevi/lombardia/como</c>), without query.</summary>
    [MaxLength(SignupAttributionRules.MaxLandingPathLength)]
    public string? LandingPath { get; set; }

    /// <summary>Host of the site the visitor came from (e.g. <c>www.google.com</c>), never the full URL.</summary>
    [MaxLength(SignupAttributionRules.MaxReferrerHostLength)]
    public string? ReferrerHost { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
