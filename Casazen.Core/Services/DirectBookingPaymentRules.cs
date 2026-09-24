using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// Payment options of the public checkout that depend on the stay (BK-07, A3-16). One definition for the quote (what the
/// checkout offers) and the booking creation (what the backend accepts), so the page never offers an option the API refuses.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Free refund deadline</b> (<see cref="Booking.FreeRefundDeadline"/>): the last calendar day (Europe/Rome) on which
/// a cancellation still gets the full refund. With a <see cref="CancellationPolicy"/> on the property it is the last whole
/// day at least <see cref="CancellationPolicy.FullRefundHours"/> before the start of the check-in day (the semantics of
/// <see cref="CancellationRefundPolicy"/>); without one, the existing rule: check-in − <see cref="DefaultFreeRefundDays"/>
/// days.</item>
/// <item><b>"Paga alla scadenza"</b> (<see cref="PaymentOption.OnCancellationDeadline"/>): the card is saved at checkout and
/// charged by the deadline charge job on that day. Offered only when the day is after today in Europe/Rome: with the
/// default rule, only for an arrival more than 7 days away, whatever the length of the stay (the checkout used to offer it
/// for stays of more than 7 nights, even with the arrival tomorrow and the deadline already past).</item>
/// </list>
/// The charge itself (the job) is not changed here (task BK-08).
/// </remarks>
public static class DirectBookingPaymentRules
{
    /// <summary>Days before check-in of the free refund deadline when the property has no cancellation policy.</summary>
    public const int DefaultFreeRefundDays = 7;

    /// <summary>
    /// Whether the guest can cancel the booking by themselves. Not today: only the host cancels a booking (BK-02,
    /// <c>POST /api/bookings/{id}/cancel</c>), the guest has no self-service cancellation. So the checkout must not
    /// promise "Cancellazione gratuita fino a …" (A3-16): <see cref="DirectBookingPaymentOptions.FreeCancellationUntil"/>
    /// stays null until such a flow exists.
    /// </summary>
    public static bool GuestSelfCancellationAvailable => false;

    /// <summary>
    /// The free refund deadline of a stay (midnight UTC of the Europe/Rome date, the storage convention of date-only values).
    /// </summary>
    public static DateTime FreeRefundDeadline(DateTime checkInDate, CancellationPolicy? policy)
    {
        var checkIn = RomeCalendar.DateInRome(checkInDate);
        if (policy is null)
            return ToStoredDate(checkIn.AddDays(-DefaultFreeRefundDays));

        // Full refund while now <= start of the check-in day − FullRefundHours (CancellationRefundPolicy). The last day
        // entirely in that window is the day before the one containing that instant, daylight saving included.
        var fullRefundUntil = RomeCalendar.StartOfDayUtc(ToStoredDate(checkIn))
            .AddHours(-Math.Max(0, policy.FullRefundHours));
        return ToStoredDate(RomeCalendar.DateInRome(fullRefundUntil).AddDays(-1));
    }

    /// <summary>"Paga alla scadenza" can be chosen: the charge day is after today (both Europe/Rome calendar dates).</summary>
    public static bool IsDeferredPaymentOffered(DateTime freeRefundDeadline, DateTime todayInRome) =>
        RomeCalendar.DateInRome(freeRefundDeadline) > RomeCalendar.DateInRome(todayInRome);

    /// <summary>The payment options of a stay whose free refund deadline is <paramref name="freeRefundDeadline"/>.</summary>
    public static DirectBookingPaymentOptions OptionsFor(DateTime freeRefundDeadline, DateTime todayInRome)
    {
        var deferred = IsDeferredPaymentOffered(freeRefundDeadline, todayInRome);
        return new DirectBookingPaymentOptions(
            DeferredPaymentAvailable: deferred,
            DeferredChargeDate: deferred ? freeRefundDeadline : null,
            FreeCancellationUntil: GuestSelfCancellationAvailable ? freeRefundDeadline : null);
    }

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
