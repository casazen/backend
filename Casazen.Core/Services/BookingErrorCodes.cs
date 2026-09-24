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

    /// <summary>422: no adult among the guests, or more minors than guests.</summary>
    public const string GuestsInvalid = "booking_guests_invalid";

    /// <summary>422: a cancelled booking cannot be changed.</summary>
    public const string NotEditable = "booking_not_editable";

    /// <summary>422: dates or guests of a stay already checked in or checked out.</summary>
    public const string StayLocked = "booking_stay_locked";

    /// <summary>
    /// 422: dates or guests of a booking that the host did not enter (booking site, channel): its price, payment and
    /// deadlines come from there.
    /// </summary>
    public const string SourceLocked = "booking_update_source_locked";

    /// <summary>422: the check-in is moved to a day already past (Europe/Rome).</summary>
    public const string CheckInInPast = "booking_checkin_in_past";

    /// <summary>409: the booking is no longer waiting for a confirmation.</summary>
    public const string NotPending = "booking_not_pending";

    /// <summary>
    /// 422: the host confirms only the pending bookings entered by hand; a checkout hold is confirmed by the guest's
    /// payment.
    /// </summary>
    public const string NotConfirmable = "booking_not_confirmable";

    /// <summary>409: check-out of a booking that is not checked in.</summary>
    public const string NotCheckedIn = "booking_not_checked_in";

    /// <summary>422: check-out before the check-out day (Europe/Rome).</summary>
    public const string CheckOutTooEarly = "booking_checkout_too_early";
}
