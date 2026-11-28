using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

public static class ServiceRequestReminderRules
{
    public static readonly TimeOnly ReminderTime = new(18, 0);

    public static DateTime DueAt(DateTime startUtc) =>
        RomeCalendar.ToUtc(RomeCalendar.DateInRome(startUtc).AddDays(-1), ReminderTime);

    public static bool IsDue(DateTime startUtc, DateTime? takenAtUtc, DateTime nowUtc) =>
        nowUtc >= DueAt(startUtc) && nowUtc < startUtc && (takenAtUtc is not { } taken || WillBeSent(startUtc, taken));

    public static bool WillBeSent(DateTime startUtc, DateTime takenAtUtc) => takenAtUtc < DueAt(startUtc);
}
