using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// When and for how long the automatic cleaning of a stay happens (E24/F01/US01, NF-120): one place for the setup of a property, the automation and the
/// tests. Times are wall-clock times on the clock of the property, as the time triggers of the routines read them.
/// </summary>
public static class CleaningSchedule
{
    /// <summary>How long a cleaning lasts when the setup says nothing: the length a request without a catalog service gets (<c>EnsureTimedCreateCommandAsync</c>).</summary>
    public const int DefaultDurationMinutes = 120;

    /// <summary>The time a cleaning starts when neither the setup nor the property says one: the time a service request of a stay gets when the host names none.</summary>
    public static readonly TimeOnly FallbackStartTime = new(10, 0);

    /// <summary>The time the cleaning starts: the one of the setup, else the check-out time of the property, else <see cref="FallbackStartTime"/>.</summary>
    public static TimeOnly StartTimeOf(TimeOnly? setupStartTime, TimeOnly? propertyCheckOutBy) =>
        setupStartTime ?? propertyCheckOutBy ?? FallbackStartTime;

    /// <summary>How long the cleaning lasts: the length of the setup, else <see cref="DefaultDurationMinutes"/>.</summary>
    public static int DurationOf(int? setupDurationMinutes) => setupDurationMinutes ?? DefaultDurationMinutes;

    /// <summary>
    /// The instant (UTC) the cleaning of a stay starts: the check-out day of the stay (a calendar date stored as midnight UTC, as the time triggers read it)
    /// at <see cref="StartTimeOf"/>, on the clock of the property (<paramref name="timeZoneId"/>, Europe/Rome when it has none or the machine does not know it).
    /// </summary>
    public static DateTime StartUtc(DateTime checkOutDate, TimeOnly? setupStartTime, TimeOnly? propertyCheckOutBy, string? timeZoneId) =>
        RomeCalendar.ToUtc(
            DateOnly.FromDateTime(checkOutDate),
            StartTimeOf(setupStartTime, propertyCheckOutBy),
            RomeCalendar.ZoneOf(timeZoneId));
}

/// <summary>How a call of <see cref="ICleaningAutomationService"/> ended.</summary>
public enum CleaningAutomationOutcome
{
    /// <summary>The cleaning was created (a service request or a staff activity).</summary>
    Created = 1,

    /// <summary>The stay had its automatic cleaning already: nothing more was created (the key of the idempotency).</summary>
    AlreadyCreated = 2,

    /// <summary>The stay already has a cleaning someone asked for (a request or an activity that is not cancelled): the automation adds none.</summary>
    AlreadyPlanned = 3,

    /// <summary>The property has no setup, or an inactive one: nothing is created and the "To do" of the stay that ended without a cleaning appears as today.</summary>
    NotConfigured = 4,

    /// <summary>The stay does not happen (a request, a hold, cancelled, a no-show): it gets no cleaning.</summary>
    StayNotActive = 5,

    /// <summary>The cleaning could not be created: <see cref="CleaningAutomationResult.Code"/> says why. The host is told with a "To do" and an alert.</summary>
    Failed = 6,

    /// <summary>The dates of the stay changed and the cleaning moved with them.</summary>
    Moved = 7,

    /// <summary>The stay was cancelled and its cleaning, not started yet, was cancelled with it.</summary>
    Cancelled = 8,

    /// <summary>The cleaning is already taken by the supplier (or has an offer waiting): the host decides, with a "To do" and an alert.</summary>
    HostToDecide = 9,

    /// <summary>Nothing to do: the stay has no automatic cleaning, its date did not change, or the cleaning is started, done or over.</summary>
    Unchanged = 10,

    /// <summary>The routines are off (<c>Features:Routines</c>): the automation reads and writes nothing.</summary>
    ModuleOff = 11,
}

/// <summary>What a call of <see cref="ICleaningAutomationService"/> did.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Code">Why it failed or was not done, a stable code (<see cref="CleaningAutomationCodes"/>, or the code of the service request error that stopped it); <c>null</c> otherwise.</param>
/// <param name="ServiceRequestId">The service request of the cleaning, for a supplier.</param>
/// <param name="InternalActivityId">The staff activity of the cleaning, for staff.</param>
public sealed record CleaningAutomationResult(
    CleaningAutomationOutcome Outcome,
    string? Code = null,
    Guid? ServiceRequestId = null,
    Guid? InternalActivityId = null);

/// <summary>The stable codes of <see cref="ICleaningAutomationService"/>: what a routine records instead of a text, never carrying a name or any other personal data.</summary>
public static class CleaningAutomationCodes
{
    /// <summary>The booking of the event is not one of the org.</summary>
    public const string StayNotFound = "cleaning_stay_not_found";

    /// <summary>The staff member of the setup is not an active member of the org who reaches the property any more.</summary>
    public const string StaffUnavailable = "cleaning_staff_unavailable";

    /// <summary>The setup is not one the automation can run (a supplier with no price, a cleaner that is not the one its kind says): it is saved by the API, so this is a row written by hand.</summary>
    public const string SetupInvalid = "cleaning_setup_invalid";

    /// <summary>
    /// The cleaning could not be created because of an error that is not a rule (a database error, a timeout, a queue that refuses): nothing was saved, and the host is told like for any other
    /// failure. The code says no more than that, because the routine engine runs a routine once for each event and the event is not delivered to it again.
    /// </summary>
    public const string CreationFailed = "cleaning_creation_failed";

    /// <summary>
    /// The setup is a supplier's and the property is not in euro (its currency changed after the setup was saved, or the row was written by hand): the requests to the suppliers, their prices and
    /// their payments are in euro and nothing is converted, so no request is made and the host is told like for any other failure.
    /// </summary>
    public const string SupplierCurrencyUnsupported = "cleaning_supplier_currency_unsupported";
}

/// <summary>
/// The cleaning at check-out for the stays of an org (E24/F01/US01/T02, NF-120, decision O11): creates the cleaning the default cleaner of the property owes a
/// stay, and keeps it in step with the stay. Called by the routine action <c>CreateCleaningRequest</c> and by the consumer of the changes of a stay
/// (<c>CleaningBookingChangesHandler</c>); every call is for one org and reads and writes in it only (a booking of another org is not found).
/// </summary>
/// <remarks>
/// <para>Everything is behind <c>Features:Routines</c>: with the module closed no call reads or writes anything (<see cref="CleaningAutomationOutcome.ModuleOff"/>).</para>
/// <para><b>Once for each stay.</b> The creation is serialized for the stay and keyed by a <c>StayCleaning</c> row written in the same transaction as the
/// request or the activity, so two triggers (the time of the check-out and the departure the host registered) create one cleaning. The creation and both follow-ups take the
/// lock of the stay first and read the stay, the setup and the key after it, so a change that comes while the cleaning is being made waits for it and finds it.</para>
/// </remarks>
public interface ICleaningAutomationService
{
    /// <summary>
    /// Creates the cleaning of the stay <paramref name="bookingId"/> of <paramref name="orgId"/> for the default cleaner of its property: for a supplier a
    /// service request (category cleaning, the booking and the property, the check-out day at the time of the setup on the clock of the property, the duration,
    /// the agreed price as its estimated amount and a note «prezzo concordato») that the supplier takes with the existing flow; for staff a staff activity of
    /// type Cleaning assigned to the member, at the same time, with the cost and the recharge to the owner. A stay that does not happen, a property with no active
    /// setup and a stay that has a cleaning already create nothing (see <see cref="CleaningAutomationOutcome"/>). A cleaning that cannot be created
    /// (<see cref="CleaningAutomationOutcome.Failed"/>) leaves the "To do" of the stay without a cleaning and an alert to the people of the property: whether a rule
    /// refused it (the code of the rule) or an unexpected error stopped it (<see cref="CleaningAutomationCodes.CreationFailed"/>, nothing saved); it never counts on the event
    /// being delivered again. Only a cancelled call throws.
    /// </summary>
    Task<CleaningAutomationResult> CreateForStayAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dates of the stay changed: the automatic cleaning moves to the new check-out day when nobody took it yet (a staff activity not started, a request
    /// that waits for its answer with no offer and no backup); a cleaning the supplier already took, or has an offer waiting, stays as it is and the host decides
    /// (a "To do" and an alert). A cleaning that is started, done or cancelled is left alone.
    /// </summary>
    Task<CleaningAutomationResult> FollowStayDatesAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The stay was cancelled or marked as a no-show (it does not happen): its automatic cleaning is cancelled with the reason «prenotazione annullata» unless the work
    /// started (the supplier is told by the existing notice of a cancellation). The stay is read as it is now, after the lock of the stay: an event that comes late, for a
    /// cancellation that was undone since (the stay is due again), cancels nothing. A cleaning that is started, done or cancelled is left alone.
    /// </summary>
    Task<CleaningAutomationResult> FollowStayCancellationAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);
}
