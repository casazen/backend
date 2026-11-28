using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ICheckoutOutcomeService
{
    Task<CheckoutOutcome> GetOutcomeAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The client secret of the booking's own PaymentIntent or SetupIntent, read again from Stripe, so the guest pays the
    /// same hold instead of creating a new booking that its own hold would block (409 on the dates).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="CheckoutOutcomeErrorCodes.LinkInvalid"/>.</exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="CheckoutOutcomeErrorCodes.HoldExpired"/> or <see cref="CheckoutOutcomeErrorCodes.PaymentNotResumable"/>.
    /// </exception>
    Task<CheckoutPaymentSession> ResumePaymentAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);
}

/// <summary>The checkout of a booking as its guest sees it. No personal data of the guest.</summary>
/// <param name="ExpiresAt">Until when the guest can pay, or the request waits; null when nothing is pending.</param>
/// <param name="DeferredChargeDate">"Paga alla scadenza": the day the saved card is charged.</param>
/// <param name="BookingCode">The booking code of "Le mie prenotazioni" (BK-11), formatted.</param>
public sealed record CheckoutOutcome(
    Guid BookingId,
    CheckoutOutcomeState State,
    PaymentOption PaymentOption,
    Guid PropertyId,
    string? PropertySlug,
    string PropertyName,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    decimal TotalPrice,
    string Currency,
    DateTime? ExpiresAt,
    DateOnly? DeferredChargeDate,
    string BookingCode);

/// <summary>What the Stripe Payment Element needs to pay a hold again.</summary>
/// <param name="ClientSecret">
/// Immediate payment: the PaymentIntent's client secret. "Paga alla scadenza" whose deferred charge failed (BK-08): the
/// client secret of the deferred charge PaymentIntent, confirmed by the guest on-session.
/// </param>
/// <param name="SetupIntentClientSecret">"Paga alla scadenza" not confirmed yet: the SetupIntent's client secret.</param>
/// <param name="StripeAccountId">Connected account the intent lives on (direct charges).</param>
/// <param name="ExpiresAt">
/// End of the hold, past it the dates are released; for a failed deferred charge the automatic cancellation, <c>null</c>
/// when none applies.
/// </param>
public sealed record CheckoutPaymentSession(
    Guid BookingId,
    PaymentOption PaymentOption,
    string? ClientSecret,
    string? SetupIntentClientSecret,
    string PublishableKey,
    string StripeAccountId,
    DateTime? ExpiresAt);

public static class CheckoutOutcomeErrorCodes
{
    public const string LinkInvalid = "checkout_link_invalid";

    public const string HoldExpired = "checkout_hold_expired";

    public const string PaymentNotResumable = "checkout_payment_not_resumable";
}
