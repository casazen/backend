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

    /// <summary>The daily send job emails the link to confirmed stays starting within this many days. Default 3.</summary>
    public int SendWindowDays { get; set; } = 3;

    /// <summary>
    /// Days a check-in link stays valid (A5-27). Default 7; values outside
    /// <see cref="MinSessionLifetimeDays"/>–<see cref="MaxSessionLifetimeDays"/> are brought within them.
    /// </summary>
    public int SessionLifetimeDays { get; set; } = 7;

    /// <summary>Validity of a new link.</summary>
    public TimeSpan SessionLifetime =>
        TimeSpan.FromDays(Math.Clamp(SessionLifetimeDays, MinSessionLifetimeDays, MaxSessionLifetimeDays));

    /// <summary>
    /// The send job reminds, once per stay, the guest of a confirmed stay arriving within this many days (today included) whose data are
    /// still missing (NF-40, <c>CheckIn__ReminderDaysBefore</c>). Default 1; 0 or less turns the reminder off.
    /// </summary>
    public int ReminderDaysBefore { get; set; } = 1;
}
