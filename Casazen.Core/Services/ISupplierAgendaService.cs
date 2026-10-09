using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The weekly working hours of a supplier: every band, Monday first then by start, and when the supplier last saved
/// hours with at least one band (<c>null</c> while it has none).
/// </summary>
public sealed record SupplierHours(IReadOnlyList<SupplierWeeklyBand> Bands, DateTime? ConfiguredAt);

/// <summary>A day closed by hand or by the calendar feed (<c>SupplierAvailability</c> with <c>Available = false</c>).</summary>
public sealed record SupplierClosedDay(DateOnly Date, SupplierAvailabilitySource Source);

/// <summary>
/// A service request as an item of the agenda: its day and, since SP-04, its hours when it has them. Only what a calendar
/// tile needs, never the property, the address or a contact (those are the request's detail, and the supplier sees them
/// only after taking it).
/// </summary>
/// <param name="Date">
/// The Europe/Rome day of the work: the day of <paramref name="StartUtc"/> for a request with a time, else the check-out day
/// of the stay of a short-rent request.
/// </param>
/// <param name="StartUtc">First instant of the work (UTC); <c>null</c> for a request that only has a day.</param>
/// <param name="EndUtc">Instant the work ends (UTC); <c>null</c> for a request that only has a day.</param>
public sealed record SupplierAgendaRequest(
    Guid Id,
    DateOnly Date,
    ServiceRequestStatus Status,
    string Category,
    DateTime? StartUtc = null,
    DateTime? EndUtc = null)
{
    /// <summary>True when the request has hours of its own (<see cref="StartUtc"/> and <see cref="EndUtc"/>).</summary>
    public bool HasHours => StartUtc is not null && EndUtc is not null;
}

/// <summary>
/// The two settings of the supplier that the booking from the public showcase reads (SP-10): whether it takes bookings online
/// (<c>SupplierSettings.OnlineBookingEnabled</c>, off until the supplier turns it on) and how long it has to answer a new request
/// (<c>RespondWithinMinutes</c>, decision D8: 180). A supplier that never saved a setting has the defaults, which is "no online
/// bookings".
/// </summary>
public sealed record SupplierBookingSettings(bool OnlineBookingEnabled, int RespondWithinMinutes)
{
    /// <summary>What a supplier that never saved a setting has.</summary>
    public static SupplierBookingSettings Default { get; } = new(false, SupplierAgendaDefaults.RespondWithinMinutes);
}

/// <summary>
/// What the supplier's calendar shows for a range of days (<c>GET api/supplier/calendar</c>): the working hours, the days
/// closed, the time off, the blocks, extra openings and engagements that touch the range, and the requests as items of a
/// whole day.
/// </summary>
public sealed record SupplierCalendar(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SupplierWeeklyBand> WorkingHours,
    IReadOnlyList<SupplierClosedDay> ClosedDays,
    IReadOnlyList<SupplierTimeOff> TimeOff,
    IReadOnlyList<SupplierBusyWindow> Windows,
    IReadOnlyList<SupplierAgendaRequest> Requests);

/// <summary>
/// The supplier's agenda (SP-03, <c>api/supplier/availability/*</c> and <c>api/supplier/calendar</c>): weekly working
/// hours, time off, blocks and extra openings, the rules (buffer, jobs a day, notice, horizon, slot step), the calendar the
/// console shows and the input of the slot planner (<see cref="SupplierSlotPlanner"/>).
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> Every method takes the supplier org id (resolved from the caller's own supplier link, never from
/// the request) and reads and writes only the rows of that org: the tables are keyed by the supplier org and are not
/// tenant-filtered, so every query carries an explicit <c>OrgId</c> predicate. A time off or a window of another supplier is
/// 404.</para>
/// <para><b>Errors.</b> <see cref="Exceptions.NotFoundException"/> (404, <see cref="SupplierAgendaErrors.TimeOffNotFound"/>,
/// <see cref="SupplierAgendaErrors.BlockNotFound"/>) and <see cref="Exceptions.DomainRuleException"/> (422, the codes of
/// <see cref="SupplierAgendaErrors"/>; the ones about a value are <see cref="SupplierAgendaRuleException"/>, with the
/// fields at fault).</para>
/// <para><b>Serialization.</b> Every write takes the PostgreSQL advisory lock <c>SupplierCalendarSync</c> of the supplier
/// org (the lock of the iCal sync and of the day overrides), reads after taking it and commits at the end, so the limits
/// and the checks are never decided on a stale read.</para>
/// </remarks>
public interface ISupplierAgendaService
{
    /// <summary>The weekly working hours. A supplier that never saved any has no band and no <c>ConfiguredAt</c>. Read-only.</summary>
    Task<SupplierHours> GetHoursAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the weekly working hours with <paramref name="input"/> (a weekday that is not in it becomes a rest day) and
    /// sets <c>ConfiguredAt</c> to now, or to <c>null</c> when no band is left.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="SupplierAgendaErrors.HoursInvalid"/> (with the fields).</exception>
    Task<SupplierHours> ReplaceHoursAsync(Guid supplierOrgId, SupplierHoursInput input, CancellationToken cancellationToken = default);

    /// <summary>The time off that has not ended yet (last day today or later), by first day. Read-only.</summary>
    Task<IReadOnlyList<SupplierTimeOff>> ListTimeOffAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>Adds a time off.</summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="SupplierAgendaErrors.TimeOffInvalid"/> (with the fields) or <see cref="SupplierAgendaErrors.TimeOffLimitReached"/>.
    /// </exception>
    Task<SupplierTimeOff> AddTimeOffAsync(Guid supplierOrgId, SupplierTimeOffInput input, CancellationToken cancellationToken = default);

    /// <summary>Deletes a time off (the days it closed are open again).</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="SupplierAgendaErrors.TimeOffNotFound"/>.</exception>
    Task DeleteTimeOffAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>The blocks and extra openings the supplier set by hand that have not ended yet, by start. Read-only.</summary>
    Task<IReadOnlyList<SupplierBusyWindow>> ListBlocksAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>Adds a manual block or extra opening.</summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="SupplierAgendaErrors.BlockInvalid"/> (with the fields) or <see cref="SupplierAgendaErrors.BlockLimitReached"/>.
    /// </exception>
    Task<SupplierBusyWindow> AddBlockAsync(Guid supplierOrgId, SupplierBlockInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a manual block or extra opening. The windows of the supplier's calendar feed (SP-05) cannot be deleted from
    /// here: they answer like a window that is not there.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="SupplierAgendaErrors.BlockNotFound"/>.</exception>
    Task DeleteBlockAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules of the supplier's agenda; the defaults (<see cref="SupplierAgendaDefaults"/>) while it never saved one. A
    /// read never creates the settings row. Read-only.
    /// </summary>
    Task<SupplierPlanningRules> GetRulesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the five rules the console edits (the other settings of the supplier stay as they are).</summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="SupplierAgendaErrors.RulesInvalid"/> (with the fields).</exception>
    Task<SupplierPlanningRules> ReplaceRulesAsync(Guid supplierOrgId, SupplierRulesInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// The settings the booking from the public showcase reads (SP-10): <c>OnlineBookingEnabled</c> and
    /// <c>RespondWithinMinutes</c>. The defaults while the supplier never saved a setting; a read never creates the settings row.
    /// Read-only.
    /// </summary>
    Task<SupplierBookingSettings> GetBookingSettingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the supplier's calendar shows from <paramref name="from"/> to <paramref name="to"/> (Europe/Rome days, both
    /// included, at most <see cref="SupplierAgendaLimits.MaxCalendarDays"/>): hours, closed days, time off, windows and the
    /// requests that have a day. Read-only.
    /// </summary>
    Task<SupplierCalendar> GetCalendarAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The input of the slot planner for the days from <paramref name="from"/> to <paramref name="to"/>: the rules, the
    /// weekly hours, the time off, the closed days, the extra openings, and everything that takes the supplier's time today
    /// (manual blocks, engagements of the calendar feed, requests with hours as <see cref="SupplierOccupancy.TimedRequest"/>,
    /// requests that only have a day as <see cref="SupplierOccupancy.DatedRequest"/>). The tasks that add a source of occupied
    /// time (holds) add their <see cref="SupplierOccupancy"/> here.
    /// </summary>
    Task<SupplierPlanningInput> BuildPlanningInputAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="BuildPlanningInputAsync(Guid, DateOnly, DateOnly, CancellationToken)"/> without the request
    /// <paramref name="exceptRequestId"/>: the planning input to move that request, which must not be in its own way (its
    /// current hours and its place in the day's count are left out).
    /// </summary>
    Task<SupplierPlanningInput> BuildPlanningInputAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        Guid? exceptRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>The plan of every day from <paramref name="from"/> to <paramref name="to"/> for <paramref name="query"/>.</summary>
    Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        SupplierSlotQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="PlanAsync(Guid, DateOnly, DateOnly, SupplierSlotQuery, CancellationToken)"/> to move the request
    /// <paramref name="exceptRequestId"/> (see <see cref="BuildPlanningInputAsync(Guid, DateOnly, DateOnly, Guid?, CancellationToken)"/>).
    /// </summary>
    Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        SupplierSlotQuery query,
        Guid? exceptRequestId,
        CancellationToken cancellationToken = default);
}
