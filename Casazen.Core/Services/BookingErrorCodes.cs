namespace Casazen.Core.Services;

public static class BookingErrorCodes
{
    public const string DatesUnavailable = "booking_dates_unavailable";

    public const string CreateInvalid = "booking_create_invalid";

    public const string CalendarRangeInvalid = "booking_calendar_range_invalid";

    public const string InvalidDates = "booking_invalid_dates";

    public const string TooManyGuests = "booking_too_many_guests";

    public const string GuestsInvalid = "booking_guests_invalid";

    public const string NotEditable = "booking_not_editable";

    public const string StayLocked = "booking_stay_locked";

    public const string SourceLocked = "booking_update_source_locked";

    public const string CheckInInPast = "booking_checkin_in_past";

    public const string NotPending = "booking_not_pending";

    public const string NotConfirmable = "booking_not_confirmable";

    public const string NotCheckedIn = "booking_not_checked_in";

    public const string CheckOutTooEarly = "booking_checkout_too_early";

    public const string AlreadyCheckedOut = "booking_already_checked_out";

    public const string ArrivalNotRegistered = "booking_arrival_not_registered";

    public const string NotConfirmed = "booking_not_confirmed";

    public const string AlreadyCheckedIn = "booking_already_checked_in";

    public const string ArrivalTooEarly = "booking_arrival_too_early";

    public const string ArrivalAfterDeparture = "booking_arrival_after_departure";

    public const string DepartureNotConfirmed = "checkout_departure_not_confirmed";

    public const string TurnoverRequestInvalid = "checkout_service_request_invalid";

    public const string CheckoutWizardNotStarted = "checkout_wizard_not_started";

    public const string CleaningChoiceInvalid = "checkout_cleaning_choice_invalid";

    public const string CheckoutStepInvalid = "checkout_step_invalid";

    public const string CheckoutNotCompleted = "checkout_not_completed";

    public const string AlreadyCancelled = "booking_already_cancelled";

    public const string PriceOverrideInvalid = "booking_price_override_invalid";

    public const string BlockDatesMismatch = "booking_block_dates_mismatch";

    public const string OtaAmountsReadonly = "ota_amounts_readonly";

    public const string ChannelCancelNotAllowed = "octorate_channel_cancel_not_allowed";

    public const string NotCancellable = "booking_not_cancellable";

    /// <summary>
    /// 422: the money of an edit of a booking of the booking site could not be settled (E08/F02/US01/T01, NF-75): Stripe refused the refund or the
    /// charge of the saved card, or the payment link for the difference could not be made. The edit and the money are one decision: nothing is
    /// changed, nothing is saved, and asking again with the same request is safe (the idempotency key makes Stripe return what it already did).
    /// </summary>
    public const string SettlementFailed = "booking_settlement_failed";

    public const string EditPaymentInProgress = "booking_edit_payment_in_progress";

    public const string EditPaymentPermissionRequired = "booking_edit_payment_permission_required";

    public const string TouristTaxUnavailable = "booking_tourist_tax_unavailable";

    public const string OtaBookingChangeNotAllowed = "ota_booking_change_not_allowed";

    public const string OtaBookingCancelNotAllowed = "ota_booking_cancel_not_allowed";

    /// <summary>
    /// 422: Octorate refused to cancel the reservation of a channel booking, or could not be asked (no connected account, a property that is not
    /// mirrored, a mapping write-blocked, the flag off): the booking is as it was, in CasaZen and on the channel (E08/F02/US02/T02, NF-76). An
    /// Octorate that does not answer is a 503 instead (<see cref="OctorateSyncErrorCodes.Unavailable"/>). Same members as
    /// <see cref="OtaBookingChangeNotAllowed"/>.
    /// </summary>
    public const string OctorateCancelFailed = "octorate_cancel_failed";
}
