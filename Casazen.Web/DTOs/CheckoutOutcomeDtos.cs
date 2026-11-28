using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class CheckoutTokenRequest
{
    [Required(ErrorMessage = "CheckoutTokenRequired")]
    [MaxLength(128, ErrorMessage = "CheckoutTokenRequired")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// The checkout of a booking as its guest sees it on the outcome page (BK-07, A3-15). <c>state</c> is the real one (see
/// <see cref="CheckoutOutcomeState"/>); no personal data of the guest. <c>bookingCode</c> is the code of "Le mie
/// prenotazioni" (BK-11), shown with a link to that page.
/// </summary>
public sealed record CheckoutOutcomeResponse(
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
    string BookingCode)
{
    public static CheckoutOutcomeResponse From(CheckoutOutcome outcome) => new(
        outcome.BookingId,
        outcome.State,
        outcome.PaymentOption,
        outcome.PropertyId,
        outcome.PropertySlug,
        outcome.PropertyName,
        outcome.CheckInDate,
        outcome.CheckOutDate,
        outcome.NumberOfAdults,
        outcome.NumberOfChildren,
        outcome.TotalPrice,
        outcome.Currency,
        outcome.ExpiresAt,
        outcome.DeferredChargeDate,
        outcome.BookingCode);
}

public sealed record CheckoutPaymentSessionResponse(
    Guid BookingId,
    PaymentOption PaymentOption,
    string? ClientSecret,
    string? SetupIntentClientSecret,
    ConnectedAccountPublishableContext ConnectedAccountPublishableContext,
    DateTime? ExpiresAt)
{
    public static CheckoutPaymentSessionResponse From(CheckoutPaymentSession session) => new(
        session.BookingId,
        session.PaymentOption,
        session.ClientSecret,
        session.SetupIntentClientSecret,
        new ConnectedAccountPublishableContext
        {
            PublishableKey = session.PublishableKey,
            StripeAccountId = session.StripeAccountId,
        },
        session.ExpiresAt);
}
