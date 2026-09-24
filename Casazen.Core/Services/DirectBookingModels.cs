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
    string Currency);

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
    DateTime? OnSiteRequestExpiresAt = null);

public class DirectBookingException(string message, string ErrorCode) : Exception(message)
{
    public string ErrorCode { get; } = ErrorCode;

    /// <summary>Values of the localized message (e.g. the capacity for <see cref="DirectBookingErrorCodes.TooManyGuests"/>).</summary>
    public object[] MessageArgs { get; init; } = [];
}

public static class DirectBookingErrorCodes
{
    public const string PropertyNotFound = "property_not_found";
    public const string PaymentNotReady = "payment_not_ready";
    public const string NotAvailable = "not_available";
    public const string InvalidConsentVersion = "invalid_consent_version";
    public const string TooManyGuests = "too_many_guests";
    public const string InvalidDates = "invalid_dates";
    public const string InvalidPaymentOption = "invalid_payment_option";
    public const string StripeError = "stripe_error";

    /// <summary>422: the tourist tax depends on the age of the minors and their ages are missing (BK-03).</summary>
    public const string ChildAgesRequired = "tourist_tax_child_ages_required";
}
