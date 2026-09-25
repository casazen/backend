namespace Casazen.Core.Services;

using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

public record DirectBookingGuestInput(
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    string Country);

public record DirectBookingCreateInput(
    Guid PropertyId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    DirectBookingGuestInput Guest,
    string ConsentVersion,
    string ConsentIpAddress,
    string? SpecialRequests,
    PaymentOption PaymentOption = PaymentOption.Immediate,
    IReadOnlyList<int>? ChildrenAges = null);

/// <summary>Stay to price before booking (checkout quote).</summary>
/// <param name="ChildrenAges">Age of each minor at check-in, needed when the tourist tax depends on it.</param>
public record DirectBookingQuoteInput(
    Guid PropertyId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    IReadOnlyList<int>? ChildrenAges = null);

/// <summary>
/// Price of a direct booking: <c>TotalPrice = BasePrice + tourist tax</c> when the tax is calculated, otherwise
/// <c>BasePrice</c> (the tax is then not included and never invented). The tax is collected with the rest of the
/// total (spec-direct-checkout AC6).
/// </summary>
/// <param name="FreeRefundDeadline">
/// Last day of the full refund (<see cref="DirectBookingPaymentRules.FreeRefundDeadline"/>), recorded on the booking; also
/// the day the deferred payment is charged.
/// </param>
/// <param name="PaymentOptions">Which payment options the checkout may offer for this stay (A3-16).</param>
public record DirectBookingQuote(
    Guid PropertyId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Nights,
    decimal NightlyRate,
    decimal CleaningFee,
    decimal BasePrice,
    TouristTaxQuote TouristTax,
    decimal TotalPrice,
    string Currency,
    DateTime FreeRefundDeadline,
    DirectBookingPaymentOptions PaymentOptions);

/// <summary>Payment options of a stay, decided by the backend so the checkout never offers or promises what does not apply.</summary>
/// <param name="DeferredPaymentAvailable">
/// "Paga alla scadenza" (<see cref="PaymentOption.OnCancellationDeadline"/>) can be chosen: its charge day is after today in
/// Europe/Rome (<see cref="DirectBookingPaymentRules.IsDeferredPaymentOffered"/>).
/// </param>
/// <param name="DeferredChargeDate">Day the saved card is charged when the deferred payment is chosen (midnight UTC of the Rome date).</param>
/// <param name="FreeCancellationUntil">
/// Last day the guest can cancel for free by themselves; <c>null</c> while the guest has no self-service cancellation
/// (<see cref="DirectBookingPaymentRules.GuestSelfCancellationAvailable"/>): the checkout then promises no free cancellation.
/// </param>
public record DirectBookingPaymentOptions(
    bool DeferredPaymentAvailable,
    DateTime? DeferredChargeDate,
    DateTime? FreeCancellationUntil);

public record DirectBookingCreateResult(
    Guid BookingId,
    string ClientSecret,
    string PublishableKey,
    string StripeAccountId,
    decimal Amount,
    string Currency,
    decimal TouristTaxAmount,
    decimal BasePrice,
    string? SetupIntentClientSecret = null,
    DateTime? FreeRefundDeadline = null,
    PaymentOption PaymentOption = PaymentOption.Immediate,
    TouristTaxQuoteStatus TouristTaxStatus = TouristTaxQuoteStatus.Calculated,
    DateTime? OnSiteRequestExpiresAt = null,
    string CheckoutToken = "");

/// <summary>
/// Stable <c>code</c> values of the public checkout errors (<c>POST /api/public/bookings</c> and its quote, BK-06 / R-11,
/// BK-07): the frontend translates them (<c>apiErrors.codes.*</c>) instead of showing a generic "checkout failed". Dates
/// and guests reuse the booking codes (<see cref="BookingErrorCodes"/>); "pay at the property" requests have theirs in
/// <see cref="OnSiteRequestErrorCodes"/>; the outcome page in <see cref="CheckoutOutcomeErrorCodes"/>.
/// </summary>
public static class DirectBookingErrorCodes
{
    /// <summary>409: the host has not completed Stripe Connect: the site does not take bookings yet (R-11).</summary>
    public const string PaymentsNotReady = "direct_booking_payments_not_ready";

    /// <summary>422: check-in in the past, check-out not after check-in, or a stay longer than the longest one priced.</summary>
    public const string InvalidStay = "direct_booking_invalid_stay";

    /// <summary>400: the data processing consent was not given.</summary>
    public const string ConsentRequired = "direct_booking_consent_required";

    /// <summary>422: the consent text changed since the page was loaded.</summary>
    public const string ConsentOutdated = "direct_booking_consent_outdated";

    /// <summary>422: unknown payment option.</summary>
    public const string InvalidPaymentOption = "direct_booking_invalid_payment_option";

    /// <summary>
    /// 422: "Paga alla scadenza" chosen for a stay whose charge day is not after today (arrival too close, A3-16,
    /// <see cref="DirectBookingPaymentRules.IsDeferredPaymentOffered"/>).
    /// </summary>
    public const string DeferredPaymentUnavailable = "direct_booking_deferred_payment_unavailable";

    /// <summary>422: the tourist tax depends on the age of the minors and their ages are missing (BK-03).</summary>
    public const string ChildAgesRequired = "tourist_tax_child_ages_required";
}
