namespace Casazen.Core.Options;

/// <summary>
/// Guest check-in links (US-020, CO-09), section <c>CheckIn</c> (on Railway <c>CheckIn__SendWindowDays</c>,
/// <c>CheckIn__SessionLifetimeDays</c>). Runbook <c>docs/runbooks/alloggiati.md</c>.
/// </summary>
public class GuestCheckInOptions
{
    public const string SectionName = "CheckIn";

    public const int MinSessionLifetimeDays = 1;
    public const int MaxSessionLifetimeDays = 60;

    public string? PrivacyNoticeVersion { get; set; }

    public string? MarketingConsentVersion { get; set; }

    public int SendWindowDays { get; set; } = 3;

    public int SessionLifetimeDays { get; set; } = 7;

    public TimeSpan SessionLifetime =>
        TimeSpan.FromDays(Math.Clamp(SessionLifetimeDays, MinSessionLifetimeDays, MaxSessionLifetimeDays));

    public int ReminderDaysBefore { get; set; } = 1;
}
