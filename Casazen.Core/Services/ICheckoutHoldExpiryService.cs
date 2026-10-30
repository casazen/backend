namespace Casazen.Core.Services;

/// <summary>
/// Expires the holds of the public checkout past <c>DirectBooking:PendingTtlMinutes</c> (<see cref="CheckoutHolds.IsExpired"/>),
/// one at a time under a row lock, so two runs never expire the same hold twice (BK-21, A3-13). For each hold the
/// PaymentIntent or SetupIntent is cancelled first, on the connected account it was created on; only then the booking is
/// cancelled with <see cref="Entities.BookingCancellationReason.CheckoutHoldExpired"/> and its uncollected payments
/// <see cref="Entities.PaymentStatus.Canceled"/>. When Stripe says the guest has already paid
/// or is paying (<c>succeeded</c>, <c>processing</c>, <c>requires_capture</c>) the booking is left to the payment
/// webhook and its payment row is marked <see cref="Entities.PaymentStatus.Processing"/>, which keeps the dates taken.
/// "Pay at the property" requests past their deadline (BK-06, <see cref="OnSiteRequests"/>) have no intent: they are
/// cancelled with <see cref="Entities.BookingCancellationReason.OnSiteEmailNotConfirmed"/> or
/// <see cref="Entities.BookingCancellationReason.OnSiteRequestExpired"/> (the guest then gets an email).
/// </summary>
public interface ICheckoutHoldExpiryService
{
    /// <summary>Every expired hold (the recurring <c>checkout-hold-expiry</c> job).</summary>
    Task<CheckoutHoldExpiryRun> ExpireDueHoldsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The expired holds of one property whose stay overlaps [<paramref name="checkIn"/>, <paramref name="checkOut"/>),
    /// before a new booking takes those dates. Holds of other dates are left to the job.
    /// </summary>
    Task<CheckoutHoldExpiryRun> ExpireOverlappingHoldsAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken cancellationToken = default);

    /// <summary>Most bookings handled by one <see cref="CloseOpenPaymentsOfCancelledBookingsAsync"/> run; the rest waits for the next one.</summary>
    const int CancelledBookingsBatchSize = 50;

    /// <summary>
    /// The payments a cancelled booking left open (the recurring <c>checkout-hold-expiry</c> job, after the expiry). A payment of a
    /// <see cref="Entities.BookingStatus.Cancelled"/> booking is never money to collect, yet cancellations that did not carry their
    /// payments (the cleanup before the BK-21 job changed only the booking, and any path added since) left them
    /// <see cref="Entities.PaymentStatus.Pending"/> or <see cref="Entities.PaymentStatus.Failed"/>, listed as "to collect" for ever.
    /// For each such booking, oldest first and at most <see cref="CancelledBookingsBatchSize"/> per run, under its row lock: the
    /// intents are released on the connected account they live on, exactly as the expiry does, and then those payments become
    /// <see cref="Entities.PaymentStatus.Canceled"/>. The booking itself is not touched (not its status, nor when or why it was
    /// cancelled). When Stripe says the guest has paid or is paying, nothing is cancelled: a PaymentIntent's payment row becomes
    /// <see cref="Entities.PaymentStatus.Processing"/>, which records the answer and needs a person (no job settles a payment of a
    /// cancelled booking: what to do with a late payment is the payment webhook's decision). Idempotent: a closed booking leaves the set.
    /// </summary>
    Task<CancelledBookingPaymentsRun> CloseOpenPaymentsOfCancelledBookingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one expiry run.</summary>
/// <param name="Expired">Holds cancelled with their intents.</param>
/// <param name="LeftToWebhook">Holds whose guest has paid or is paying on Stripe: not cancelled.</param>
/// <param name="Skipped">Holds taken by a concurrent run, or no longer expired when locked.</param>
/// <param name="Failed">Holds not expired because of an error (Stripe unreachable, unexpected intent state): retried by the next run.</param>
public sealed record CheckoutHoldExpiryRun(int Expired, int LeftToWebhook, int Skipped, int Failed)
{
    public static CheckoutHoldExpiryRun Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>Outcome of one <see cref="ICheckoutHoldExpiryService.CloseOpenPaymentsOfCancelledBookingsAsync"/> run.</summary>
/// <param name="Closed">Cancelled bookings whose open payments became <see cref="Entities.PaymentStatus.Canceled"/>.</param>
/// <param name="InFlight">Cancelled bookings whose guest has paid or is paying on Stripe: not closed, a person has to look.</param>
/// <param name="Skipped">Bookings taken by a concurrent run, or with nothing open left when locked.</param>
/// <param name="Failed">Bookings not closed because of an error (Stripe unreachable, unexpected intent state): retried by the next run.</param>
public sealed record CancelledBookingPaymentsRun(int Closed, int InFlight, int Skipped, int Failed)
{
    public static CancelledBookingPaymentsRun Empty { get; } = new(0, 0, 0, 0);
}
