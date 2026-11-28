using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public static class CleaningSchedule
{
    public const int DefaultDurationMinutes = 120;

    public static readonly TimeOnly FallbackStartTime = new(10, 0);

    public static TimeOnly StartTimeOf(TimeOnly? setupStartTime, TimeOnly? propertyCheckOutBy) =>
        setupStartTime ?? propertyCheckOutBy ?? FallbackStartTime;

    public static int DurationOf(int? setupDurationMinutes) => setupDurationMinutes ?? DefaultDurationMinutes;

    public static DateTime StartUtc(DateTime checkOutDate, TimeOnly? setupStartTime, TimeOnly? propertyCheckOutBy, string? timeZoneId) =>
        RomeCalendar.ToUtc(
            DateOnly.FromDateTime(checkOutDate),
            StartTimeOf(setupStartTime, propertyCheckOutBy),
            RomeCalendar.ZoneOf(timeZoneId));
}

public enum CleaningAutomationOutcome
{
    Created = 1,

    /// <summary>The stay had its automatic cleaning already: nothing more was created (the key of the idempotency).</summary>
    AlreadyCreated = 2,

    AlreadyPlanned = 3,

    NotConfigured = 4,

    StayNotActive = 5,

    Failed = 6,

    Moved = 7,

    Cancelled = 8,

    HostToDecide = 9,

    Unchanged = 10,

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
    public const string StayNotFound = "cleaning_stay_not_found";

    public const string StaffUnavailable = "cleaning_staff_unavailable";

    public const string SetupInvalid = "cleaning_setup_invalid";

    /// <summary>
    /// The cleaning could not be created because of an error that is not a rule (a database error, a timeout, a queue that refuses): nothing was saved, and the host is told like for any other
    /// failure. The code says no more than that, because the routine engine runs a routine once for each event and the event is not delivered to it again.
    /// </summary>
    public const string CreationFailed = "cleaning_creation_failed";

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

    Task<CleaningAutomationResult> FollowStayDatesAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The stay was cancelled or marked as a no-show (it does not happen): its automatic cleaning is cancelled with the reason «prenotazione annullata» unless the work
    /// started (the supplier is told by the existing notice of a cancellation). The stay is read as it is now, after the lock of the stay: an event that comes late, for a
    /// cancellation that was undone since (the stay is due again), cancels nothing. A cleaning that is started, done or cancelled is left alone.
    /// </summary>
    Task<CleaningAutomationResult> FollowStayCancellationAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);
}
