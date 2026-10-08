namespace Casazen.Core.Options;

/// <summary>
/// The events of the SEO funnel (section <c>Seo:Events</c>, Railway <c>Seo__Events__*</c>, SE-04). Runbook:
/// <c>docs/runbooks/seo-funnel.md</c>.
/// </summary>
public class SeoEventOptions
{
    public const string SectionName = "Seo:Events";

    public const int DefaultRetentionDays = 90;

    /// <summary>
    /// Days an event is kept; older ones are deleted every night. Events hold no personal data, so this is about the
    /// size of the table and the usefulness of the report, not about a legal period. A value of 0 or less reads as the
    /// default.
    /// </summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>The configured retention, never below one day.</summary>
    public int EffectiveRetentionDays => RetentionDays > 0 ? RetentionDays : DefaultRetentionDays;
}
