using Casazen.Core.Entities;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Gdpr</c> (CO-15, runbook <c>docs/runbooks/gdpr.md</c>): versions of the guest-facing privacy texts and the
/// retention period of each category of guest data. <b>Nothing has a default in code</b>: the texts and the periods are
/// legal decisions of the product owner (D14), never invented.
/// </summary>
public class GdprOptions
{
    public const string SectionName = "Gdpr";

    /// <summary>
    /// Version of the privacy notice (art. 13 GDPR) shown on the check-in portal (frontend <c>checkin.privacyNotice.*</c>).
    /// Recorded with each check-in; while empty the check-in works (legal obligation) but the notice shown is not recorded.
    /// </summary>
    public string? PrivacyNoticeVersion { get; set; }

    /// <summary>
    /// Version of the marketing consent text of the check-in portal (frontend <c>checkin.marketingConsent</c>). While empty
    /// the portal does not offer the marketing consent at all.
    /// </summary>
    public string? MarketingConsentVersion { get; set; }

    public GdprRetentionOptions Retention { get; set; } = new();

    /// <summary>The configured version, trimmed, or null when missing.</summary>
    public static string? Normalize(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Trim();
}

/// <summary>Section <c>Gdpr:Retention</c>: one period per <see cref="GuestDataCategory"/>.</summary>
public class GdprRetentionOptions
{
    public RetentionPeriodOptions DocumentScans { get; set; } = new();
    public RetentionPeriodOptions AlloggiatiData { get; set; } = new();
    public RetentionPeriodOptions Marketing { get; set; } = new();
    public RetentionPeriodOptions FiscalData { get; set; } = new();

    public RetentionPeriodOptions For(GuestDataCategory category) => category switch
    {
        GuestDataCategory.DocumentScans => DocumentScans,
        GuestDataCategory.AlloggiatiData => AlloggiatiData,
        GuestDataCategory.Marketing => Marketing,
        GuestDataCategory.FiscalData => FiscalData,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}

/// <summary>
/// Retention period of one category: <see cref="Years"/>, <see cref="Months"/> and <see cref="Days"/> added to the
/// category's reference date (e.g. the check-out), plus the <see cref="Source"/> that justifies it. The period applies only
/// when at least one amount is set, none is negative and the source is cited: otherwise the retention job deletes nothing
/// of that category and says so in the logs.
/// </summary>
public class RetentionPeriodOptions
{
    public int? Years { get; set; }
    public int? Months { get; set; }
    public int? Days { get; set; }

    /// <summary>Legal or documented source of the period (law, article, document of the product owner).</summary>
    public string? Source { get; set; }

    private bool HasAmount => Years is not null || Months is not null || Days is not null;

    private bool HasNegativeAmount => Years < 0 || Months < 0 || Days < 0;

    /// <summary>True when the period can be applied: an amount, none negative, and a source.</summary>
    public bool IsConfigured => HasAmount && !HasNegativeAmount && !string.IsNullOrWhiteSpace(Source);

    /// <summary>Why the period cannot be applied, or null when it can.</summary>
    public string? ConfigurationProblem =>
        !HasAmount ? "no period (Years, Months or Days)"
        : HasNegativeAmount ? "negative period"
        : string.IsNullOrWhiteSpace(Source) ? "no source cited (Source)"
        : null;

    /// <summary>The end of the period that starts on <paramref name="start"/>.</summary>
    public DateTime AddTo(DateTime start) => start.AddYears(Years ?? 0).AddMonths(Months ?? 0).AddDays(Days ?? 0);

    /// <summary>
    /// Coarse SQL filter: every start whose period ended before <paramref name="today"/> is before this date. It is
    /// <paramref name="today"/> minus the period plus 3 days, because adding and subtracting months are not inverse at
    /// month ends (31 → 28); <see cref="HasEnded"/> then decides exactly.
    /// </summary>
    public DateTime CandidateStartBefore(DateTime today) =>
        today.Date.AddYears(-(Years ?? 0)).AddMonths(-(Months ?? 0)).AddDays(-(Days ?? 0)).AddDays(3);

    /// <summary>True when the period starting on <paramref name="start"/> ended before the calendar day <paramref name="today"/>.</summary>
    public bool HasEnded(DateTime start, DateTime today) => AddTo(start.Date) < today.Date;
}
