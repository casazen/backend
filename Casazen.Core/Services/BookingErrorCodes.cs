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

    /// <summary>400: the last day of the host calendar range is before its first day (MO-06).</summary>
    public const string CalendarRangeInvalid = "booking_calendar_range_invalid";

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

    /// <summary>409: check-out of a booking that is neither checked in nor confirmed (pending, cancelled).</summary>
    public const string NotCheckedIn = "booking_not_checked_in";

    /// <summary>
    /// 422: check-out before the check-in day (Europe/Rome). An early departure of a stay already started is allowed
    /// (CO-08: one rule for the wizard and <c>POST /check-out</c>).
    /// </summary>
    public const string CheckOutTooEarly = "booking_checkout_too_early";

    /// <summary>409: the check-out of the booking is already recorded.</summary>
    public const string AlreadyCheckedOut = "booking_already_checked_out";

    /// <summary>
    /// 409: check-out of a confirmed booking whose arrival was never registered: the host confirms the arrival
    /// ("registra arrivo e procedi", <c>registerArrival: true</c>) and the check-out goes on (CO-08).
    /// </summary>
    public const string ArrivalNotRegistered = "booking_arrival_not_registered";

    /// <summary>409: the arrival is registered only for a confirmed booking (pending, cancelled, checked out).</summary>
    public const string NotConfirmed = "booking_not_confirmed";

    /// <summary>409: the arrival of the booking is already registered (double click, another device).</summary>
    public const string AlreadyCheckedIn = "booking_already_checked_in";

    /// <summary>422: arrival registered before the check-in day (Europe/Rome).</summary>
    public const string ArrivalTooEarly = "booking_arrival_too_early";

    /// <summary>
    /// 422: arrival registered after the check-out day (Europe/Rome): the stay is closed with the check-out, which
    /// registers the arrival too.
    /// </summary>
    public const string ArrivalAfterDeparture = "booking_arrival_after_departure";

    /// <summary>422: the check-out wizard is completed without confirming that the guest left.</summary>
    public const string DepartureNotConfirmed = "checkout_departure_not_confirmed";

    /// <summary>422: the turnover request of the check-out wizard could not be created (supplier, category).</summary>
    public const string TurnoverRequestInvalid = "checkout_service_request_invalid";

    /// <summary>
    /// 409: the check-out wizard is completed, or its progress saved, before it was started
    /// (<c>checkout-wizard/start</c>, CO-17).
    /// </summary>
    public const string CheckoutWizardNotStarted = "checkout_wizard_not_started";

    /// <summary>
    /// 422: the cleaning step of the check-out wizard is contradictory: a supplier with "skip", or "request" without a
    /// supplier (CO-17).
    /// </summary>
    public const string CleaningChoiceInvalid = "checkout_cleaning_choice_invalid";

    /// <summary>422: the step of the check-out wizard is not one of its 5 steps (CO-17).</summary>
    public const string CheckoutStepInvalid = "checkout_step_invalid";

    /// <summary>409: the property is declared ready before the check-out of the stay (CO-17).</summary>
    public const string CheckoutNotCompleted = "checkout_not_completed";
}
