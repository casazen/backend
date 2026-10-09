using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// When the customer who booked a supplier from its public showcase is reminded of the work (SP-10, job
/// <c>service-request-reminders</c>): at <b>18:00 Europe/Rome of the day before</b> the day of the work, once. Pure functions on
/// instants, no clock: the hour is the wall clock of Rome, so the reminder follows the daylight saving time
/// (<see cref="RomeCalendar.ToUtc(DateOnly, TimeOnly)"/>).
/// </summary>
public static class ServiceRequestReminderRules
{
    /// <summary>The hour of the reminder, on the wall clock of Europe/Rome.</summary>
    public static readonly TimeOnly ReminderTime = new(18, 0);

    /// <summary>
    /// The first instant (UTC) the reminder of a work that starts at <paramref name="startUtc"/> is due: 18:00 in Rome on the day
    /// before the Rome day of the start. A work at 08:00 and one at 20:00 of the same day have the same reminder.
    /// </summary>
    public static DateTime DueAt(DateTime startUtc) =>
        RomeCalendar.ToUtc(RomeCalendar.DateInRome(startUtc).AddDays(-1), ReminderTime);

    /// <summary>
    /// True when the reminder is to be sent at <paramref name="nowUtc"/>: its time has come, the work has not started, and the
    /// request was taken before that time (a request taken after 18:00 of the day before was told "accepted" a moment ago, and a
    /// second e-mail about the same work would only repeat it). Whether it was already sent is the request's
    /// <c>ReminderSentAt</c>, not decided here.
    /// </summary>
    public static bool IsDue(DateTime startUtc, DateTime? takenAtUtc, DateTime nowUtc) =>
        nowUtc >= DueAt(startUtc) && nowUtc < startUtc && (takenAtUtc is not { } taken || WillBeSent(startUtc, taken));

    /// <summary>
    /// True when a request taken at <paramref name="takenAtUtc"/> for a work that starts at <paramref name="startUtc"/> gets a
    /// reminder: it was taken before the reminder time. The e-mail that tells the customer the request was accepted promises the
    /// reminder only then (decision D24: nothing is promised that is not sent).
    /// </summary>
    public static bool WillBeSent(DateTime startUtc, DateTime takenAtUtc) => takenAtUtc < DueAt(startUtc);
}
