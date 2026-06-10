namespace Casazen.Core.Services;

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
    string? SpecialRequests);

public record DirectBookingCreateResult(
    Guid BookingId,
    string ClientSecret,
    string PublishableKey,
    string StripeAccountId,
    decimal Amount,
    string Currency,
    decimal TouristTaxAmount,
    decimal BasePrice);

public class DirectBookingException(string message, string ErrorCode) : Exception(message)
{
    public string ErrorCode { get; } = ErrorCode;
}

public static class DirectBookingErrorCodes
{
    public const string PropertyNotFound = "property_not_found";
    public const string PaymentNotReady = "payment_not_ready";
    public const string NotAvailable = "not_available";
    public const string InvalidConsentVersion = "invalid_consent_version";
    public const string TooManyGuests = "too_many_guests";
    public const string InvalidDates = "invalid_dates";
    public const string StripeError = "stripe_error";
}
