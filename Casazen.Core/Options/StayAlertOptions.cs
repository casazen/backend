namespace Casazen.Core.Options;

/// <summary>
/// Stages of the host alerts about a stay (CO-10), section <c>StayAlerts</c>. Every hour of day is Europe/Rome; the
/// sequence and the resulting maximum of messages are documented in <c>docs/runbooks/alloggiati.md</c>.
/// </summary>
public class StayAlertOptions
{
    public const string SectionName = "StayAlerts";

    /// <summary>
    /// Hour (Europe/Rome) of the day before arrival at which the host is told that guest data are missing
    /// (Alloggiati workflow: "1 giorno prima: avviso al proprietario se mancano dati"). Default 10.
    /// </summary>
    public int GuestDataReminderHourLocal { get; set; } = 10;

    /// <summary>
    /// Hours before the deadline of the "deadline approaching" alert, never before the start of the arrival day (the
    /// portal refuses a communication before it). Default 12: noon of the arrival day when the arrival time is not
    /// registered.
    /// </summary>
    public int DeadlineWarningHours { get; set; } = 12;

    /// <summary>
    /// Daily reminders after the "overdue" alert, at <see cref="OverdueReminderHourLocal"/> of the following days.
    /// 0 disables them. Default 2.
    /// </summary>
    public int MaxOverdueReminders { get; set; } = 2;

    /// <summary>Hour (Europe/Rome) of the daily overdue reminders. Default 9.</summary>
    public int OverdueReminderHourLocal { get; set; } = 9;

    /// <summary>
    /// Maximum Alloggiati deadline messages for one stay: guest data missing, deadline approaching, overdue and the
    /// daily reminders.
    /// </summary>
    public int MaxAlloggiatiDeadlineMessages => 3 + Math.Max(0, MaxOverdueReminders);
}
