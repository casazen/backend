using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Stable <c>code</c> values of the errors of the scheduled change of rental mode (FD-05, PM-02): part of the API
/// contract, never rename one. Each has the key of its localized message (<c>SharedResources</c>, Italian and English).
/// The same codes are the <c>issues</c> of a preview and the <see cref="PropertyModeChange.FailureReason"/> of a change
/// the job could not apply.
/// </summary>
public static class PropertyModeErrorCodes
{
    /// <summary>404: the property does not exist or is of another org.</summary>
    public const string PropertyNotFound = "property_not_found";

    /// <summary>422: the property is already in the mode asked for (the mode is exclusive: there is nothing to change).</summary>
    public const string AlreadyInMode = "property_mode_unchanged";

    /// <summary>422: the day is today or in the past. The change starts at midnight of Rome: the first day is tomorrow.</summary>
    public const string DateTooEarly = "property_mode_date_too_early";

    /// <summary>422: the day is more than <see cref="PropertyModeRules.MaxYearsAhead"/> years away (a typo, not a plan).</summary>
    public const string DateTooFar = "property_mode_date_too_far";

    /// <summary>
    /// 409: to long-term, a stay (pending, confirmed or checked in) or a block imported from a portal calendar ends on or
    /// after the day. The message carries the first day that is free.
    /// </summary>
    public const string BlockedByBookings = "property_mode_blocked_by_bookings";

    /// <summary>
    /// 409: to short stays, a lease that is not a draft nor rejected ends on or after the day. The message carries the first
    /// day that is free (<c>EndDate</c> + 1, decision D16: termination and notice are entered by hand later).
    /// </summary>
    public const string BlockedByLease = "property_mode_blocked_by_lease";

    /// <summary>409: to short stays, the property has a draft lease: it must be deleted first (decision D16).</summary>
    public const string BlockedByDraftLease = "property_mode_blocked_by_draft_lease";

    /// <summary>409: the property already has a change waiting for its day (one at a time).</summary>
    public const string ChangeExists = "property_mode_change_exists";

    /// <summary>404: no change with that id on the property (or of another org).</summary>
    public const string ChangeNotFound = "property_mode_change_not_found";

    /// <summary>409: the change is not waiting any more (applied, cancelled or failed): only a scheduled one is withdrawn.</summary>
    public const string ChangeNotScheduled = "property_mode_change_not_scheduled";

    /// <summary>400: the target of a preview is not <c>short</c> or <c>long</c>.</summary>
    public const string TargetInvalid = "property_mode_target_invalid";

    /// <summary>
    /// Failure reason only: on its day the property was not in the mode the change started from (somebody put it back by
    /// hand, runbook). Nothing is applied.
    /// </summary>
    public const string PropertyChanged = "property_mode_changed";

    public const string PropertyNotFoundMessageKey = "PropertyNotFound";
    public const string AlreadyInModeMessageKey = "PropertyModeAlreadySet";
    public const string DateTooEarlyMessageKey = "PropertyModeDateTooEarly";
    public const string DateTooFarMessageKey = "PropertyModeDateTooFar";
    public const string BlockedByBookingsMessageKey = "PropertyModeBlockedByBookings";
    public const string BlockedByLeaseMessageKey = "PropertyModeBlockedByLease";
    public const string BlockedByDraftLeaseMessageKey = "PropertyModeBlockedByDraftLease";
    public const string ChangeExistsMessageKey = "PropertyModeChangeExists";
    public const string ChangeNotFoundMessageKey = "PropertyModeChangeNotFound";
    public const string ChangeNotScheduledMessageKey = "PropertyModeChangeNotScheduled";
    public const string TargetInvalidMessageKey = "PropertyModeTargetInvalid";
}

/// <summary>What stands in the way of a change of mode (<see cref="PropertyModeBlocker.Kind"/>).</summary>
public enum PropertyModeBlockerKind
{
    /// <summary>A stay of the property: pending, confirmed or checked in, whatever its source (the site, the host, a portal).</summary>
    Stay = 0,

    /// <summary>
    /// A block imported from a portal calendar (iCal feed) that is not already counted through an OTA stay: the portals'
    /// reservations arrive as blocks. Wait for it or remove the feed.
    /// </summary>
    ImportedBlock = 1,

    /// <summary>A lease that is not a draft nor rejected: it keeps the property in long-term mode until its last day.</summary>
    Lease = 2,

    /// <summary>A draft lease: no date frees the property from it, it must be deleted (decision D16).</summary>
    DraftLease = 3,
}

/// <summary>
/// One stay, block or lease that stands in the way of a change of mode. Ids, dates and the status only: no name, e-mail
/// or text of a guest, a tenant or a portal reaches the client.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">The booking, the calendar block or the lease.</param>
/// <param name="Start">Arrival of the stay, first night of the block, start of the lease (stay dates, no time).</param>
/// <param name="End">Departure of the stay, first free day of the block, last day of the lease (stay dates, no time).</param>
/// <param name="FreeFrom">
/// The first day the property can change mode without being in its way: the day after <paramref name="End"/>.
/// <c>null</c> for a draft lease, which no day frees (it is deleted).
/// </param>
/// <param name="Status">Booking status or lease status (the enum name); <c>null</c> for a block.</param>
/// <param name="Source">Booking source (<c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>…) or channel of the feed of the block; <c>null</c> for a lease.</param>
public sealed record PropertyModeBlocker(
    PropertyModeBlockerKind Kind,
    Guid Id,
    DateTime Start,
    DateTime End,
    DateTime? FreeFrom,
    string? Status = null,
    string? Source = null);

/// <summary>
/// What a change to <see cref="PropertyModePreview.To"/> on <see cref="PropertyModePreview.Date"/> would meet today: the
/// answer of the preview, computed by the same rules (<see cref="PropertyModeRules"/>) the creation and the daily job use.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="From">Its mode now.</param>
/// <param name="To">The mode asked for.</param>
/// <param name="Today">Today, Europe/Rome (midnight UTC of that date).</param>
/// <param name="EarliestDate">The first day the change can start: tomorrow, or the day after the last thing in its way.</param>
/// <param name="Date">The day evaluated: the one asked for, or <paramref name="EarliestDate"/> when none was.</param>
/// <param name="Blockers">What stands in the way of <paramref name="Date"/>, by start date. Empty when nothing does.</param>
/// <param name="Issues">
/// The <see cref="PropertyModeErrorCodes"/> that would refuse the change on <paramref name="Date"/>. Empty = it can be scheduled.
/// </param>
/// <param name="ScheduledChange">The change already waiting for its day on this property, if any.</param>
public sealed record PropertyModePreview(
    Guid PropertyId,
    RentalMode From,
    RentalMode To,
    DateTime Today,
    DateTime EarliestDate,
    DateTime Date,
    IReadOnlyList<PropertyModeBlocker> Blockers,
    IReadOnlyList<string> Issues,
    PropertyModeChange? ScheduledChange)
{
    /// <summary>True when a change on <see cref="Date"/> would be accepted.</summary>
    public bool CanSchedule => Issues.Count == 0;
}

/// <summary>The mode of a property and what happens to it: the change waiting for its day and the last one that finished.</summary>
/// <param name="Mode">The mode now.</param>
/// <param name="Scheduled">The change waiting for its day (at most one).</param>
/// <param name="Last">The latest change that is no longer waiting (applied, cancelled or failed), by creation.</param>
public sealed record PropertyModeState(
    Guid PropertyId,
    RentalMode Mode,
    PropertyModeChange? Scheduled,
    PropertyModeChange? Last);

/// <summary>What a run of <see cref="IPropertyModeService.ApplyDueAsync"/> did.</summary>
/// <param name="Skipped">Another run was in progress (session advisory lock): nothing was done.</param>
/// <param name="Examined">The changes that were due.</param>
/// <param name="Applied">The ones now applied.</param>
/// <param name="Failed">The ones that failed on their day, plus those that could not be processed (they stay scheduled).</param>
public sealed record PropertyModeRunResult(bool Skipped, int Examined, int Applied, int Failed);

/// <summary>
/// The scheduled change of rental mode of a property (PM-02, decisions D16 and D19; <c>gap/06</c> §4.4): the owner
/// programs the passage from short stays to long-term leases, or back, for a day; the property changes mode by itself at
/// midnight of Rome, never over a stay or a lease. This service is, after the creation of the property, <b>the only writer
/// of <see cref="Property.RentalMode"/></b>. The caller authorizes the property (TN-3); the tenant filter hides another
/// org's rows. Runbook: <c>docs/runbooks/property-rental-mode.md</c>.
/// </summary>
/// <remarks>
/// The rules, in <see cref="PropertyModeRules"/>: to long-term, the first day is the day after the last departure of the
/// stays (pending, confirmed, checked in) and the end of the blocks imported from the portals; to short stays, the day
/// after the last day of the last lease that is not a draft nor rejected, with the drafts deleted first. In both cases not
/// before tomorrow. The creation, the cancellation and the application run under the property dates lock
/// (<c>BookingRepository.LockPropertyDatesAsync</c>), the one every booking takes.
/// </remarks>
public interface IPropertyModeService
{
    /// <summary>
    /// What a change to <paramref name="to"/> meets: the first day possible and what stands in the way of
    /// <paramref name="date"/> (the first day possible when none is given). Reads only; nothing is reserved, so the answer
    /// can be out of date by the time the change is created, which checks again.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="PropertyModeErrorCodes.PropertyNotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="PropertyModeErrorCodes.AlreadyInMode"/>.</exception>
    Task<PropertyModePreview> PreviewAsync(
        Guid propertyId,
        RentalMode to,
        DateTime? date = null,
        CancellationToken cancellationToken = default);

    /// <summary>The mode of the property, the change waiting for its day and the last finished one.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="PropertyModeErrorCodes.PropertyNotFound"/>.</exception>
    Task<PropertyModeState> GetStateAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Programs the change of the property to <paramref name="to"/> on <paramref name="effectiveDate"/> and tells the host
    /// by e-mail. Under the property dates lock: the stays, the imported blocks and the leases are read after every booking
    /// in progress has finished, and a booking that comes later finds the change.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="PropertyModeErrorCodes.PropertyNotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="PropertyModeErrorCodes.AlreadyInMode"/>, <see cref="PropertyModeErrorCodes.DateTooEarly"/>,
    /// <see cref="PropertyModeErrorCodes.DateTooFar"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="PropertyModeErrorCodes.ChangeExists"/>, <see cref="PropertyModeErrorCodes.BlockedByBookings"/>,
    /// <see cref="PropertyModeErrorCodes.BlockedByLease"/>, <see cref="PropertyModeErrorCodes.BlockedByDraftLease"/>.
    /// </exception>
    Task<PropertyModeChange> ScheduleAsync(
        Guid propertyId,
        RentalMode to,
        DateTime effectiveDate,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws a change that is still waiting for its day. Nothing changes on the property.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="PropertyModeErrorCodes.ChangeNotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="PropertyModeErrorCodes.ChangeNotScheduled"/>.</exception>
    Task CancelAsync(Guid propertyId, Guid changeId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The hourly run (<c>property-mode-change</c> job): applies every change whose day has come (today in Europe/Rome or
    /// before), checking the stays, blocks and leases again; a change that no longer fits is
    /// <see cref="PropertyModeChangeStatus.Failed"/> and the host is told. Idempotent: a change is applied once, whatever
    /// the number of runs, retries or concurrent runs; one run at a time (a session advisory lock on top of Hangfire's).
    /// </summary>
    Task<PropertyModeRunResult> ApplyDueAsync(CancellationToken cancellationToken = default);
}
