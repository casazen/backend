using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The outcome page of the public checkout (BK-07, A3-15): the real state of the booking and, while the hold is valid, the
/// payment to complete again (a failed card, a redirect method that came back, a page closed before paying). Access with
/// the checkout token only (<see cref="CheckoutOutcomes"/>); errors are <see cref="Exceptions.NotFoundException"/> and
/// <see cref="Exceptions.DomainConflictException"/> with the codes of <see cref="CheckoutOutcomeErrorCodes"/>.
/// </summary>
public interface ICheckoutOutcomeService
{
    /// <summary>The outcome of the checkout of <paramref name="bookingId"/>.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="CheckoutOutcomeErrorCodes.LinkInvalid"/>: wrong id or token.</exception>
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
/// <param name="ClientSecret">Immediate payment: the PaymentIntent's client secret.</param>
/// <param name="SetupIntentClientSecret">"Paga alla scadenza": the SetupIntent's client secret.</param>
/// <param name="StripeAccountId">Connected account the intent lives on (direct charges).</param>
/// <param name="ExpiresAt">End of the hold: past it the dates are released.</param>
public sealed record CheckoutPaymentSession(
    Guid BookingId,
    PaymentOption PaymentOption,
    string? ClientSecret,
    string? SetupIntentClientSecret,
    string PublishableKey,
    string StripeAccountId,
    DateTime ExpiresAt);

/// <summary>Stable error codes of the checkout outcome page (frontend <c>apiErrors.codes.*</c>).</summary>
public static class CheckoutOutcomeErrorCodes
{
    /// <summary>404: no checkout matches the booking id and token (the same answer for both, no enumeration).</summary>
    public const string LinkInvalid = "checkout_link_invalid";

    /// <summary>409: the hold expired (or its intent was cancelled): the dates were released, the payment cannot resume.</summary>
    public const string HoldExpired = "checkout_hold_expired";

    /// <summary>409: nothing to pay again: already paid or being paid, confirmed, cancelled, or a "pay at the property" request.</summary>
    public const string PaymentNotResumable = "checkout_payment_not_resumable";
}
