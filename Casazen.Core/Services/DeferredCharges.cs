using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// Rules of the deferred payment of the public checkout ("Paga alla scadenza", <see cref="PaymentOption.OnCancellationDeadline"/>),
/// task BK-08 (A3-14). The card (or other method) saved by the checkout's SetupIntent is charged off-session by the
/// <c>direct-booking-charge</c> job from the day of <see cref="Booking.FreeRefundDeadline"/>:
/// <list type="bullet">
/// <item>the payment is <see cref="PaymentStatus.Completed"/> only when Stripe says <c>succeeded</c> (answer or webhook),
/// <see cref="PaymentStatus.Processing"/> while it is <c>processing</c> (the webhook completes it), and
/// <see cref="PaymentStatus.Failed"/> when the guest has to act (<c>requires_action</c>, <c>requires_payment_method</c>:
/// authentication required, card declined);</item>
/// <item>at most one attempt per booking per Europe/Rome day and <see cref="GetMaxAttempts"/> attempts in total;</item>
/// <item>on the first failure the guest gets a link to pay on the checkout outcome page, the host an email;</item>
/// <item>still unpaid <see cref="GetCancelAfterDays"/> days after that failure, and before the check-in day, the booking is
/// cancelled with <see cref="BookingCancellationReason.DeferredPaymentNotCompleted"/> and its dates released.</item>
/// </list>
/// The two numbers are PROVISIONAL technical defaults, not product rules: the product owner decides them
/// (<c>docs/runbooks/direct-booking.md</c> § 8, DUBBI BK-08).
/// </summary>
public static class DeferredCharges
{
    /// <summary><c>metadata.kind</c> of the deferred charge PaymentIntents (read by the webhooks).</summary>
    public const string Kind = "direct-booking-deadline-charge";

    /// <summary>Description of the deferred payment row created by the checkout (the SetupIntent, then its PaymentIntent).</summary>
    public const string PaymentDescription = "Direct checkout - deferred payment (charged at deadline)";

    /// <summary>Description of deferred charges recorded before the checkout created the row itself.</summary>
    public const string LegacyPaymentDescription = "Direct booking - charged at deadline";

    public const string MaxAttemptsSetting = "DirectBooking:DeferredChargeMaxAttempts";
    public const string CancelAfterDaysSetting = "DirectBooking:DeferredChargeCancelAfterDays";

    /// <summary>
    /// PROVISIONAL technical default (not a product rule, BK-08 DUBBI): off-session attempts in total, one per day, so that a
    /// declined card is never retried forever (card networks limit retries of declined payments).
    /// </summary>
    public const int ProvisionalMaxAttempts = 3;

    /// <summary>
    /// PROVISIONAL technical default (not a product rule, BK-08 DUBBI): days after the first failure after which an unpaid
    /// booking is cancelled, so that it does not hold its dates unpaid until the arrival. Equal to the default number of
    /// attempts, so every attempt runs before the cancellation. <c>0</c> disables the automatic cancellation.
    /// </summary>
    public const int ProvisionalCancelAfterDays = 3;

    /// <summary>Off-session attempts in total (at least 1).</summary>
    public static int GetMaxAttempts(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(MaxAttemptsSetting, ProvisionalMaxAttempts));

    /// <summary>Days from the first failure to the automatic cancellation; <c>null</c> when disabled (0 or less).</summary>
    public static int? GetCancelAfterDays(IConfiguration configuration)
    {
        var days = configuration.GetValue(CancelAfterDaysSetting, ProvisionalCancelAfterDays);
        return days > 0 ? days : null;
    }

    /// <summary>Whether <paramref name="payment"/> is the deferred payment of <paramref name="booking"/>.</summary>
    public static bool IsDeferredPayment(Booking booking, Payment payment) =>
        payment.BookingId == booking.Id &&
        (payment.Description is PaymentDescription or LegacyPaymentDescription ||
         (!string.IsNullOrEmpty(booking.StripeSetupIntentId) &&
          string.Equals(payment.TransactionId, booking.StripeSetupIntentId, StringComparison.Ordinal)));

    /// <summary>
    /// The payment row of the deferred charge of <paramref name="booking"/>: a collected one when there is one (already
    /// paid, possibly refunded since), otherwise the most recent one. <c>null</c> for bookings recorded without it.
    /// </summary>
    public static Payment? FindPayment(Booking booking, IEnumerable<Payment> payments) =>
        payments
            .Where(p => IsDeferredPayment(booking, p))
            .OrderByDescending(p => IsCollected(p.Status))
            .ThenByDescending(p => p.UpdatedAt)
            .FirstOrDefault();

    /// <summary>The money was collected (possibly refunded afterwards): never charged again.</summary>
    public static bool IsCollected(PaymentStatus status) =>
        status is PaymentStatus.Completed or PaymentStatus.Refunded or PaymentStatus.PartiallyRefunded;

    /// <summary>
    /// Payment status for a PaymentIntent status. <c>requires_action</c> and <c>requires_payment_method</c> (and
    /// <c>requires_confirmation</c>) need the guest: <see cref="PaymentStatus.Failed"/>, never Completed.
    /// </summary>
    public static PaymentStatus StatusOf(string? paymentIntentStatus) => paymentIntentStatus switch
    {
        "succeeded" => PaymentStatus.Completed,
        "processing" or "requires_capture" => PaymentStatus.Processing,
        "canceled" => PaymentStatus.Canceled,
        _ => PaymentStatus.Failed,
    };

    /// <summary>
    /// Idempotency key of the creation of the deferred charge PaymentIntent: bound to the booking, to its deadline (a new
    /// deadline is a new charge) and to the attempt, so a retry of the same attempt (Hangfire retry, crash before the
    /// commit) gets the same PaymentIntent from Stripe.
    /// </summary>
    public static string CreationIdempotencyKey(Guid bookingId, DateTime deadline, int attempt) =>
        $"direct-booking-deadline:{bookingId:N}:{RomeCalendar.DateInRome(deadline):yyyyMMdd}:{attempt}";

    /// <summary>Idempotency key of a new off-session confirmation of an existing deferred charge PaymentIntent.</summary>
    public static string ConfirmationIdempotencyKey(string paymentIntentId, int attempt) =>
        $"direct-booking-deadline-confirm:{paymentIntentId}:{attempt}";

    /// <summary>
    /// The deferred charge failed and waits for the guest: the outcome page shows "Pagamento non riuscito" with the
    /// payment to complete (<see cref="CheckoutOutcomeState.PaymentFailed"/>). Needs the booking's payments.
    /// </summary>
    public static bool AwaitsGuestPayment(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.PaymentOption == PaymentOption.OnCancellationDeadline &&
               booking.DeferredChargeFailedAt is not null &&
               booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut &&
               FindPayment(booking, booking.Payments)?.Status == PaymentStatus.Failed;
    }

    /// <summary>
    /// Europe/Rome day on which a booking still unpaid is cancelled: the day of the first failure plus
    /// <paramref name="cancelAfterDays"/>, only when that day is before the check-in day (from the arrival on the stay may
    /// have started: the host decides). <c>null</c> when nothing failed, the cancellation is disabled or does not apply.
    /// </summary>
    public static DateOnly? CancellationDay(Booking booking, int? cancelAfterDays)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return CancellationDay(booking.DeferredChargeFailedAt, booking.CheckInDate, cancelAfterDays);
    }

    /// <inheritdoc cref="CancellationDay(Booking, int?)"/>
    public static DateOnly? CancellationDay(DateTime? failedAt, DateTime checkInDate, int? cancelAfterDays)
    {
        if (cancelAfterDays is not { } days || failedAt is not { } failed)
            return null;

        var day = RomeCalendar.DateInRome(failed).AddDays(days);
        return day < RomeCalendar.DateInRome(checkInDate) ? day : null;
    }

    /// <summary>
    /// Until when the guest can pay before the automatic cancellation: the start (Europe/Rome) of the
    /// <see cref="CancellationDay"/>, as a UTC instant. <c>null</c> when no cancellation applies.
    /// </summary>
    public static DateTime? PayByUtc(Booking booking, int? cancelAfterDays) =>
        CancellationDay(booking, cancelAfterDays) is { } day
            ? RomeCalendar.StartOfDayUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            : null;
}
