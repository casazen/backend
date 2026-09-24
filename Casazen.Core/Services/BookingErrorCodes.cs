namespace Casazen.Core.Services;

/// <summary>
/// Stable <c>code</c> values of the host booking errors (FD-05). They are part of the API contract: the frontend
/// translates them (<c>apiErrors.codes.*</c>), so never rename one.
/// </summary>
public static class BookingErrorCodes
{
    /// <summary>409: the dates overlap another active booking or an imported calendar block.</summary>
    public const string DatesUnavailable = "booking_dates_unavailable";

    /// <summary>422: the new booking breaks a rule (check-in in the past, no guests, negative price).</summary>
    public const string CreateInvalid = "booking_create_invalid";

    /// <summary>422: the check-out is not after the check-in.</summary>
    public const string InvalidDates = "booking_invalid_dates";

    /// <summary>422: more guests than the property allows.</summary>
    public const string TooManyGuests = "booking_too_many_guests";
}
