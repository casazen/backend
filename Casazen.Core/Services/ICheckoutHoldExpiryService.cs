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
