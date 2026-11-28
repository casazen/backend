using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

public sealed record SupplierHours(IReadOnlyList<SupplierWeeklyBand> Bands, DateTime? ConfiguredAt);

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
/// <param name="ServiceListingId">The catalog service the request is for, when it is for one (the conflict check reads its notice and weekdays).</param>
/// <param name="ServiceName">
/// The name of the service at the time of the request (SP-03b, a snapshot), <c>null</c> when the request is for a category only:
/// the calendar then shows <see cref="Category"/>. Filled only by <see cref="Services.ISupplierServiceRequestReader.ListForCalendarAsync"/>.
/// </param>
/// <param name="ClientLabel">
/// Who asks, as the supplier may know it (SP-03b, decision D9): the name of the host's org for a host's request; for a private
/// customer of the public showcase "Nome C." until the supplier takes the request, the full name after
/// (<see cref="SupplierJobDisclosure.CustomerName"/>). Never an address, a contact or a note. Filled only by
/// <see cref="Services.ISupplierServiceRequestReader.ListForCalendarAsync"/>.
/// </param>
/// <param name="Comune">The comune of the work as written (a property's city, or the place the customer wrote); the postal code and the street are not here. Filled only by <see cref="Services.ISupplierServiceRequestReader.ListForCalendarAsync"/>.</param>
/// <param name="Source">
/// Where the request comes from: <see cref="SupplierRequestSources.CasaZen"/> or <see cref="SupplierRequestSources.Showcase"/>. A
/// column of the request itself: both reads fill it, so it is right wherever the record comes from (the three titles above are the only
/// ones that need the join of the calendar).
/// </param>
/// <param name="HasPendingProposal">
/// True while a time the supplier proposed has no answer (the <see cref="Phase"/> is then <c>proposal_pending</c>). Filled by both
/// reads, like <paramref name="Source"/>.
/// </param>
/// <param name="HasPendingQuote">
/// True while a quote the supplier sent has no answer (SP-04b; the <see cref="Phase"/> is then <c>quote_pending</c>). Filled by both reads.
/// </param>
public sealed record SupplierAgendaRequest(
    Guid Id,
    DateOnly Date,
    ServiceRequestStatus Status,
    string Category,
    DateTime? StartUtc = null,
    DateTime? EndUtc = null,
    Guid? ServiceListingId = null,
    string? ServiceName = null,
    string? ClientLabel = null,
    string? Comune = null,
    string Source = SupplierRequestSources.CasaZen,
    bool HasPendingProposal = false,
    bool HasPendingQuote = false)
{
    public bool HasHours => StartUtc is not null && EndUtc is not null;

    public string Phase => SupplierRequestPhases.Of(Status, HasPendingProposal, HasPendingQuote);
}

/// <summary>
/// The two settings of the supplier that the booking from the public showcase reads (SP-10): whether it takes bookings online
/// (<c>SupplierSettings.OnlineBookingEnabled</c>, off until the supplier turns it on) and how long it has to answer a new request
/// (<c>RespondWithinMinutes</c>, decision D8: 180). A supplier that never saved a setting has the defaults, which is "no online
/// bookings".
/// </summary>
public sealed record SupplierBookingSettings(bool OnlineBookingEnabled, int RespondWithinMinutes)
{
    public static SupplierBookingSettings Default { get; } = new(false, SupplierAgendaDefaults.RespondWithinMinutes);
}

/// <summary>
/// The supplier's own settings as the console shows them (SP-16a, <c>GET/PUT api/supplier/settings</c>). A supplier that never saved a
/// setting has the defaults of <see cref="Default"/> (a read never creates the row).
/// </summary>
/// <param name="OnlineBookingEnabled">
/// Whether the public showcase takes bookings. <b>Read-only here</b>: only the showcase editor writes it (decision SP-D29).
/// </param>
/// <param name="AutoAcceptRegulars">The supplier accepts the requests of its regular hosts by itself (SP-04d reads it).</param>
/// <param name="NotifyNewRequests">The supplier wants a notification for every new request (SP-16d reads it).</param>
/// <param name="NotifyDayBeforeReminder">The supplier wants the reminder of tomorrow's jobs (SP-16d reads it).</param>
/// <param name="NotifyPayments">The supplier wants to know when a payment arrives (SP-16d reads it; column <c>NotifyPaymentMarked</c>).</param>
/// <param name="RespondWithinMinutes">How long a customer of the public showcase waits for the supplier's answer.</param>
public sealed record SupplierSettingsView(
    bool OnlineBookingEnabled,
    bool AutoAcceptRegulars,
    bool NotifyNewRequests,
    bool NotifyDayBeforeReminder,
    bool NotifyPayments,
    int RespondWithinMinutes)
{
    public static SupplierSettingsView Default { get; } = new(false, false, true, true, true, SupplierAgendaDefaults.RespondWithinMinutes);
}

public sealed record SupplierCalendar(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SupplierWeeklyBand> WorkingHours,
    IReadOnlyList<SupplierClosedDay> ClosedDays,
    IReadOnlyList<SupplierTimeOff> TimeOff,
    IReadOnlyList<SupplierBusyWindow> Windows,
    IReadOnlyList<SupplierAgendaRequest> Requests)
{
    public IReadOnlyList<SupplierGuestOrderAppointment> GuestOrders { get; init; } = [];
}

public sealed record SupplierGuestOrderAppointment(Guid Id, DateTime RequestedFor, string ServiceName);

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
    Task<SupplierHours> GetHoursAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<SupplierHours> ReplaceHoursAsync(Guid supplierOrgId, SupplierHoursInput input, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierTimeOff>> ListTimeOffAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<SupplierTimeOff> AddTimeOffAsync(Guid supplierOrgId, SupplierTimeOffInput input, CancellationToken cancellationToken = default);

    Task DeleteTimeOffAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierBusyWindow>> ListBlocksAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<SupplierBusyWindow> AddBlockAsync(Guid supplierOrgId, SupplierBlockInput input, CancellationToken cancellationToken = default);

    Task DeleteBlockAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default);

    Task<SupplierPlanningRules> GetRulesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<SupplierPlanningRules> ReplaceRulesAsync(Guid supplierOrgId, SupplierRulesInput input, CancellationToken cancellationToken = default);

    Task<SupplierBookingSettings> GetBookingSettingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<SupplierSettingsView> GetSettingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the five settings the console edits (SP-16a); the rest of the row stays as it is, and <b>never</b>
    /// <c>OnlineBookingEnabled</c>: only the showcase editor turns the bookings on and off (decision SP-D29). The row is created by the
    /// first write, under the agenda lock.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="SupplierAgendaErrors.SettingsInvalid"/> (with the fields).</exception>
    Task<SupplierSettingsView> ReplaceSettingsAsync(Guid supplierOrgId, SupplierSettingsInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the online bookings of the public showcase on or off (<c>SupplierSettings.OnlineBookingEnabled</c>, SP-13a). <b>The only writer</b>
    /// of that switch: the showcase content service calls it when the supplier publishes, pauses or reopens the showcase and when its payments
    /// account changes; the console's settings never do (decision SP-D29). Turning it off for a supplier that has no settings row writes
    /// nothing; turning it on creates the row. Under the agenda lock, which joins the transaction of the caller.
    /// </summary>
    Task SetOnlineBookingEnabledAsync(Guid supplierOrgId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier's urgent settings (SP-04c, <c>GET api/supplier/availability/urgent</c>): whether it accepts urgent requests, the
    /// window of the day they are accepted in and its default surcharge. <see cref="SupplierUrgentSettings.Default"/> (off, 07:00 to
    /// 22:00, no surcharge) while the supplier never saved them; a read never creates the settings row. Read-only.
    /// </summary>
    Task<SupplierUrgentSettings> GetUrgentAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the urgent settings (every field is needed); the rest of the settings row stays as it is. The row is created by the
    /// first write, under the agenda lock, which every request flow takes before it plans a slot: a booking is judged on the settings
    /// as they are saved.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="SupplierAgendaErrors.UrgentInvalid"/> (with the fields).</exception>
    Task<SupplierUrgentSettings> ReplaceUrgentAsync(Guid supplierOrgId, SupplierUrgentInput input, CancellationToken cancellationToken = default);

    Task<SupplierCalendar> GetCalendarAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

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

    Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        SupplierSlotQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        SupplierSlotQuery query,
        Guid? exceptRequestId,
        CancellationToken cancellationToken = default);
}
