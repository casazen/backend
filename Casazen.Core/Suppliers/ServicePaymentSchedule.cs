using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// When a payment is late and when the payer is reminded (SP-15b, <c>service-payment-reminders</c>): pure rules, so the daily job
/// is only a loop over them. The payer is reminded at the days of <c>SupplierPayments:ReminderDays</c> (+2 and +7) counted from the
/// first request, <b>at most</b> <see cref="ServicePaymentLimits.MaxPaymentEmails"/> emails with a link in all, never twice in a day,
/// and a payment is flagged late <c>SupplierPayments:LateAfterDays</c> (7) days after it was asked for.
/// </summary>
public static class ServicePaymentSchedule
{
    public static bool IsOwed(ServicePaymentStatus status) => status is ServicePaymentStatus.Requested or ServicePaymentStatus.Failed;

    public static DateTime? LateSince(DateTime? requestedAt, int lateAfterDays) => requestedAt?.AddDays(lateAfterDays);

    public static bool IsLate(ServicePaymentStatus status, DateTime? requestedAt, int lateAfterDays, DateTime now) =>
        IsOwed(status) && LateSince(requestedAt, lateAfterDays) is { } since && now >= since;

    /// <summary>
    /// A reminder is due: the payment is owed and its first request was sent (<paramref name="sentCount"/> is at least 1, so a
    /// payment still pending is not "reminded"), fewer than <see cref="ServicePaymentLimits.MaxPaymentEmails"/> emails went out, the
    /// last one is more than a day old, and more reminder days have passed since <paramref name="requestedAt"/> than reminders were
    /// sent (<c>sentCount - 1</c>). A reminder the supplier sent by hand counts as one of them.
    /// </summary>
    public static bool ReminderIsDue(
        ServicePaymentStatus status,
        int sentCount,
        DateTime? requestedAt,
        DateTime? lastSentAt,
        IReadOnlyList<int> reminderDays,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reminderDays);

        if (!IsOwed(status) || requestedAt is not { } asked || sentCount < 1 || sentCount >= ServicePaymentLimits.MaxPaymentEmails)
            return false;

        if (lastSentAt is { } last && now - last < TimeSpan.FromHours(ServicePaymentLimits.MinHoursBetweenRequests))
            return false;

        var daysPassed = reminderDays.Count(day => now >= asked.AddDays(day));
        var remindersSent = sentCount - 1;
        return daysPassed > remindersSent;
    }
}
