using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Validation;

namespace Casazen.Core.Entities;

/// <summary>
/// One event of the SEO funnel (SE-04, #300 AC3, A8-11): a click on the signup CTA of the SEO page of a comune, or the
/// start of the signup that came from it. Platform data, no org: it is recorded for an anonymous visitor.
/// </summary>
/// <remarks>
/// <b>No personal data.</b> The row holds the event, the comune, the marketing values of the visit
/// (<see cref="SignupAttributionRules"/>: no <c>@</c>, no URL) and the instant. Never an IP address, a user id, a visitor
/// or session id, a user agent or the full referrer: nothing in a row identifies or lets anyone recognise a person, so
/// there is nothing to dedupe by (a repeated click counts again) and no consent or hashing is involved. Rows are deleted
/// after <c>Seo:Events:RetentionDays</c> (<c>ISeoEventService.PurgeExpiredAsync</c>). Platform admins read only totals
/// per comune.
/// </remarks>
[Table("SeoEvents")]
public class SeoEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public SeoEventType Event { get; set; }

    /// <summary>ISTAT code of the comune of the page (the page sends a slug or a code, resolved when recorded).</summary>
    [Required, MaxLength(SignupAttributionRules.ComuneCodeLength)]
    public string ComuneCode { get; set; } = string.Empty;

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmSource { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmMedium { get; set; }

    [MaxLength(SignupAttributionRules.MaxUtmLength)]
    public string? UtmCampaign { get; set; }

    [MaxLength(SignupAttributionRules.MaxReferrerHostLength)]
    public string? ReferrerHost { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public enum SeoEventType
{
    CtaClick = 1,

    SignupStart = 2,
}
