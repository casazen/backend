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

    // E03/F02/US02/T01 (NF-52, CHK-12): the thresholds of the new stay stages. Proposals the product owner may change; they run only with
    // Features:ComplianceTrafficLight on, and are documented in docs/TECHNICAL.md.

    /// <summary>
    /// Hour (Europe/Rome) of the day after the communication was sent, entrusted to the channel manager or declared sent at which the host is
    /// told that no receipt is archived (<c>ReceiptMissing</c>). Default 10.
    /// </summary>
    public int ReceiptMissingHourLocal { get; set; } = 10;

    /// <summary>
    /// A stay of at most one night has the 6-hour term (<c>ShortStayAtRisk</c>): when it is not entrusted to the channel manager, the host is
    /// told when the arrival is within this many hours. Default 6.
    /// </summary>
    public int ShortStayRiskHours { get; set; } = 6;

    /// <summary>
    /// Hour (Europe/Rome) of the day before the arrival at which the host is told that a stay over 30 nights is not sent by the channel manager
    /// (<c>OverThirtyNights</c>). Default 9.
    /// </summary>
    public int OverThirtyNightsHourLocal { get; set; } = 9;

    /// <summary>
    /// Maximum Alloggiati deadline messages for one stay: guest data missing, deadline approaching, overdue and the
    /// daily reminders.
    /// </summary>
    public int MaxAlloggiatiDeadlineMessages => 3 + Math.Max(0, MaxOverdueReminders);
}
