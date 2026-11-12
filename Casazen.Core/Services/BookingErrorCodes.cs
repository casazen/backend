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

    /// <summary>
    /// 409: the booking is already cancelled (a double click, another device, a cancellation that was first). The guest's
    /// cancel link answers it with the outcome of that cancellation instead (DB-08h).
    /// </summary>
    public const string AlreadyCancelled = "booking_already_cancelled";

    /// <summary>
    /// 422: the price the host agreed for a booking entered by hand cannot be recorded (SR-06a): the lodging left once the tourist
    /// tax is taken out is negative or above <see cref="ManualBookingRules.MaxAgreedBasePrice"/>, or the note on the price is longer
    /// than <see cref="ManualBookingRules.PriceNoteMaxLength"/> characters.
    /// </summary>
    public const string PriceOverrideInvalid = "booking_price_override_invalid";

    /// <summary>422: the dates of a booking that completes a portal's block are not the dates of that block (SR-06a).</summary>
    public const string BlockDatesMismatch = "booking_block_dates_mismatch";

    /// <summary>
    /// 422: a change of a booking carries a commission, net, paid or balance amount of the channel that differs from the one stored
    /// (E02/F02/US01/T01, ADR-033-R3): those figures come from the channel manager and are never edited in CasaZen.
    /// </summary>
    public const string OtaAmountsReadonly = "ota_amounts_readonly";

    /// <summary>
    /// 422: the cancellation of a reservation that came from a portal is asked of the channel manager from CasaZen (E02/F02/US01/T01): the
    /// portal owns its cancellation, and a delete from here is not proven to be accepted.
    /// </summary>
    public const string ChannelCancelNotAllowed = "octorate_channel_cancel_not_allowed";

    /// <summary>422: the booking cannot be cancelled in its state (checked out).</summary>
    public const string NotCancellable = "booking_not_cancellable";

    /// <summary>
    /// 422: the money of an edit of a booking of the booking site could not be settled (E08/F02/US01/T01, NF-75): Stripe refused the refund or the
    /// charge of the saved card, or the payment link for the difference could not be made. The edit and the money are one decision: nothing is
    /// changed, nothing is saved, and asking again with the same request is safe (the idempotency key makes Stripe return what it already did).
    /// </summary>
    public const string SettlementFailed = "booking_settlement_failed";

    /// <summary>
    /// 409: a payment of the booking is still being processed by Stripe, or the guest is asked to pay one (a failed charge of "Paga alla scadenza"):
    /// the price of the booking is not changed until it is settled, or the money would be counted twice.
    /// </summary>
    public const string EditPaymentInProgress = "booking_edit_payment_in_progress";

    /// <summary>
    /// 403: the edit of a booking of the booking site would move money (a refund, the saved card charged, a payment link), or the quote of the edit
    /// shows it, and the caller has no <c>payment.write</c> (<c>payment.read</c> for the quote) on the property (E08/F02/US01/T01): nothing is done.
    /// </summary>
    public const string EditPaymentPermissionRequired = "booking_edit_payment_permission_required";

    /// <summary>
    /// 422: the new dates have no tourist tax rate (or one that cannot be worked out) and the booking already holds a tourist tax that the guest paid:
    /// the price would drop by the tax and the money of the edit would give it back by itself (E08/F02/US01/T01). Nothing is changed.
    /// </summary>
    public const string TouristTaxUnavailable = "booking_tourist_tax_unavailable";

    /// <summary>
    /// 422: a booking that came from a channel is changed from CasaZen where the channel does not let the host do it (E02/F02/US02/T01, D8,
    /// NF-75): nothing is changed. The text is the one sentence that says how to ask the guest or the channel; <c>guidanceKey</c> and
    /// <c>channel</c> travel with the problem.
    /// </summary>
    public const string OtaBookingChangeNotAllowed = "ota_booking_change_not_allowed";

    /// <summary>
    /// 422: a booking that came from a channel is cancelled from CasaZen where the channel does not let the host do it (E08/F02/US02/T01, D8,
    /// NF-76): nothing is changed, Octorate is not asked. Same members as <see cref="OtaBookingChangeNotAllowed"/>.
    /// </summary>
    public const string OtaBookingCancelNotAllowed = "ota_booking_cancel_not_allowed";

    /// <summary>
    /// 422: Octorate refused to cancel the reservation of a channel booking, or could not be asked (no connected account, a property that is not
    /// mirrored, a mapping write-blocked, the flag off): the booking is as it was, in CasaZen and on the channel (E08/F02/US02/T02, NF-76). An
    /// Octorate that does not answer is a 503 instead (<see cref="OctorateSyncErrorCodes.Unavailable"/>). Same members as
    /// <see cref="OtaBookingChangeNotAllowed"/>.
    /// </summary>
    public const string OctorateCancelFailed = "octorate_cancel_failed";
}
