using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The supplier's agenda (SP-03, <c>api/supplier/availability/*</c> and <c>api/supplier/calendar</c>) on
/// <see cref="SupplierWorkingHours"/>, <see cref="SupplierTimeOff"/>, <see cref="SupplierBusyWindow"/> and
/// <see cref="SupplierSettings"/>, and the bridge between those rows and the slot planner
/// (<see cref="SupplierSlotPlanner"/>).
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> The tables are keyed by the supplier org and are <b>not</b> tenant-filtered (the TN-2 allow-list says
/// why: a supplier-only account has no <c>User.OrgId</c>). Every read and write here goes through one of
/// <see cref="HoursOf"/>, <see cref="TimeOffOf"/>, <see cref="WindowsOf"/> and <see cref="SettingsOf"/>, which carry the
/// explicit <c>OrgId</c> predicate; the only other statements are inserts of rows that have their <c>OrgId</c> set. An
/// architecture test (<c>SupplierAgendaTenancyTests</c>) forbids any other code from using the tables.</para>
/// <para><b>Serialization.</b> Every write takes the advisory lock <c>SupplierCalendarSync</c> of the supplier org (the lock
/// of the iCal sync and of the day overrides, <c>CalendarSyncService.AvailabilityLock</c>) and reads the rows after taking
/// it, so the limits are never decided on a stale read and a sync never writes the same rows at once. Outside PostgreSQL
/// (in-memory tests) nothing is locked.</para>
/// <para><b>Time.</b> "Today" is the Europe/Rome day of the injected <see cref="TimeProvider"/>; instants are UTC.</para>
/// </remarks>
public class SupplierAgendaService(
    AppDbContext db,
    ISupplierServiceRequestReader requests,
    ILogger<SupplierAgendaService> logger,
    TimeProvider? timeProvider = null) : ISupplierAgendaService
{
    /// <summary>The statuses of a request that count for the day's maximum: it still is, or will be, a job of the supplier.</summary>
    private static readonly ServiceRequestStatus[] JobStatuses =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // ─── Working hours ───────────────────────────────────────────────────────────

    public async Task<SupplierHours> GetHoursAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var rows = await HoursOf(supplierOrgId).AsNoTracking().ToListAsync(cancellationToken);
        var configuredAt = await SettingsOf(supplierOrgId)
            .AsNoTracking()
            .Select(s => s.HoursConfiguredAt)
            .FirstOrDefaultAsync(cancellationToken);
        return new SupplierHours(ToBands(rows), configuredAt);
    }

    public async Task<SupplierHours> ReplaceHoursAsync(
        Guid supplierOrgId,
        SupplierHoursInput input,
        CancellationToken cancellationToken = default)
    {
        var bands = SupplierAgendaRules.NormalizeHours(input);

        await using var transaction = await LockAgendaAsync(supplierOrgId, cancellationToken);

        // By difference, not delete-all-and-insert: a band that stays is not touched (and a band that only changes its end
        // is updated), so the unique index on weekday and start never sees a row leave and come back in one save.
        var existing = await HoursOf(supplierOrgId).ToListAsync(cancellationToken);
        var wanted = bands.ToDictionary(band => (band.Weekday, band.StartMinute));
        foreach (var row in existing)
        {
            if (wanted.Remove((row.Weekday, row.StartMinute), out var kept))
                row.EndMinute = kept.EndMinute;
            else
                db.Remove(row);
        }

        foreach (var band in wanted.Values)
        {
            db.SupplierWorkingHours.Add(new SupplierWorkingHours
            {
                OrgId = supplierOrgId,
                Weekday = band.Weekday,
                StartMinute = band.StartMinute,
                EndMinute = band.EndMinute,
            });
        }

        // "Hours configured" is a fact the checklist reads: it is the moment of the last save that left at least one band.
        var now = Now();
        var settings = await EnsureSettingsAsync(supplierOrgId, now, cancellationToken);
        settings.HoursConfiguredAt = bands.Count > 0 ? now : null;
        settings.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: working hours replaced ({Bands} bands)", supplierOrgId, bands.Count);
        return new SupplierHours(bands, settings.HoursConfiguredAt);
    }

    // ─── Time off ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SupplierTimeOff>> ListTimeOffAsync(
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var today = Today();
        return await TimeOffOf(supplierOrgId)
            .AsNoTracking()
            .Where(t => t.ToDate >= today)
            .OrderBy(t => t.FromDate)
            .ThenBy(t => t.ToDate)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<SupplierTimeOff> AddTimeOffAsync(
        Guid supplierOrgId,
        SupplierTimeOffInput input,
        CancellationToken cancellationToken = default)
    {
        var today = Today();
        var content = SupplierAgendaRules.NormalizeTimeOff(input, today);

        await using var transaction = await LockAgendaAsync(supplierOrgId, cancellationToken);

        // The ones that ended are history and do not count: the limit is on what is still ahead.
        var open = await TimeOffOf(supplierOrgId).CountAsync(t => t.ToDate >= today, cancellationToken);
        if (open >= SupplierAgendaLimits.MaxTimeOffEntries)
            throw SupplierAgendaErrors.TimeOffLimit();

        var entry = new SupplierTimeOff
        {
            OrgId = supplierOrgId,
            FromDate = content.FromDate,
            ToDate = content.ToDate,
            Reason = content.Reason,
            Label = content.Label,
            CreatedAt = Now(),
        };
        db.SupplierTimeOff.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation(
            "Supplier {OrgId}: time off {TimeOffId} added ({Days} days)",
            supplierOrgId, entry.Id, entry.ToDate.DayNumber - entry.FromDate.DayNumber + 1);
        return entry;
    }

    public async Task DeleteTimeOffAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAgendaAsync(supplierOrgId, cancellationToken);

        var entry = await TimeOffOf(supplierOrgId).FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw SupplierAgendaErrors.TimeOffMissing(id);
        db.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: time off {TimeOffId} deleted", supplierOrgId, id);
    }

    // ─── Blocks and extra openings ───────────────────────────────────────────────

    public async Task<IReadOnlyList<SupplierBusyWindow>> ListBlocksAsync(
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var now = Now();
        return await ManualWindowsOf(supplierOrgId)
            .AsNoTracking()
            .Where(w => w.EndUtc > now)
            .OrderBy(w => w.StartUtc)
            .ThenBy(w => w.EndUtc)
            .ThenBy(w => w.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<SupplierBusyWindow> AddBlockAsync(
        Guid supplierOrgId,
        SupplierBlockInput input,
        CancellationToken cancellationToken = default)
    {
        var now = Now();
        var content = SupplierAgendaRules.NormalizeBlock(input, now);

        await using var transaction = await LockAgendaAsync(supplierOrgId, cancellationToken);

        var open = await ManualWindowsOf(supplierOrgId).CountAsync(w => w.EndUtc > now, cancellationToken);
        if (open >= SupplierAgendaLimits.MaxManualWindows)
            throw SupplierAgendaErrors.BlockLimit();

        var window = new SupplierBusyWindow
        {
            OrgId = supplierOrgId,
            StartUtc = content.StartUtc,
            EndUtc = content.EndUtc,
            Kind = content.Kind,
            Source = SupplierBusyWindowSource.Manual,
            Label = content.Label,
            CreatedAt = now,
        };
        db.SupplierBusyWindows.Add(window);
        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: {Kind} {WindowId} added", supplierOrgId, window.Kind, window.Id);
        return window;
    }

    public async Task DeleteBlockAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockAgendaAsync(supplierOrgId, cancellationToken);

        // Only what the supplier set by hand: an engagement of the calendar feed is the feed's, and comes back at the next sync.
        var window = await ManualWindowsOf(supplierOrgId).FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
            ?? throw SupplierAgendaErrors.BlockMissing(id);
        db.Remove(window);
        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: {Kind} {WindowId} deleted", supplierOrgId, window.Kind, id);
    }

    // ─── Rules ───────────────────────────────────────────────────────────────────

    public async Task<SupplierPlanningRules> GetRulesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var settings = await SettingsOf(supplierOrgId).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings is null ? SupplierPlanningRules.Default : ToRules(settings);
    }

    public async Task<SupplierPlanningRules> ReplaceRulesAsync(
        Guid supplierOrgId,
        SupplierRulesInput input,
        CancellationToken cancellationToken = default)
    {
        var content = SupplierAgendaRules.NormalizeRules(input);

        await using var transaction = await LockAgendaAsync(supplierOrgId, cancellationToken);

        var now = Now();
        var settings = await EnsureSettingsAsync(supplierOrgId, now, cancellationToken);
        settings.BufferMinutes = content.BufferMinutes;
        settings.MaxJobsPerDay = content.MaxJobsPerDay;
        settings.MinNoticeHours = content.MinNoticeHours;
        settings.HorizonDays = content.HorizonDays;
        settings.SlotStepMinutes = content.SlotStepMinutes;
        settings.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        logger.LogInformation("Supplier {OrgId}: agenda rules replaced", supplierOrgId);
        return ToRules(settings);
    }

    // ─── Calendar and planner ────────────────────────────────────────────────────

    public async Task<SupplierCalendar> GetCalendarAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        EnsureRange(from, to, SupplierAgendaLimits.MaxCalendarDays);

        var startUtc = RomeCalendar.StartOfDayUtc(from);
        var endUtc = RomeCalendar.StartOfDayUtc(to.AddDays(1));

        var hours = await HoursOf(supplierOrgId).AsNoTracking().ToListAsync(cancellationToken);
        var closedDays = await ClosedDaysOfAsync(supplierOrgId, from, to, cancellationToken);
        var timeOff = await TimeOffOverlappingAsync(supplierOrgId, from, to, cancellationToken);
        var windows = await WindowsOverlappingAsync(supplierOrgId, startUtc, endUtc, cancellationToken);
        var agendaRequests = await requests.ListForAgendaAsync(supplierOrgId, from, to, cancellationToken);

        return new SupplierCalendar(
            from,
            to,
            ToBands(hours),
            closedDays.Select(day => new SupplierClosedDay(day.Date, day.Source)).ToList(),
            timeOff,
            windows,
            agendaRequests);
    }

    public Task<SupplierPlanningInput> BuildPlanningInputAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        BuildPlanningInputAsync(supplierOrgId, from, to, exceptRequestId: null, cancellationToken);

    public async Task<SupplierPlanningInput> BuildPlanningInputAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        Guid? exceptRequestId,
        CancellationToken cancellationToken = default)
    {
        EnsureRange(from, to, SupplierSlotPlanner.MaxRangeDays);

        var rules = await GetRulesAsync(supplierOrgId, cancellationToken);
        var hours = await HoursOf(supplierOrgId).AsNoTracking().ToListAsync(cancellationToken);
        var closedDays = await ClosedDaysOfAsync(supplierOrgId, from, to, cancellationToken);
        var timeOff = await TimeOffOverlappingAsync(supplierOrgId, from, to, cancellationToken);

        // A day more on each side: the buffer of a job at the edge of the range reaches into the next day, and a window can
        // start the evening before.
        var windows = await WindowsOverlappingAsync(
            supplierOrgId,
            RomeCalendar.StartOfDayUtc(from.AddDays(-1)),
            RomeCalendar.StartOfDayUtc(to.AddDays(2)),
            cancellationToken);

        var occupancies = new List<SupplierOccupancy>();
        var extraOpenings = new List<SupplierInterval>();
        foreach (var window in windows)
        {
            switch (window.Kind)
            {
                case SupplierBusyWindowKind.ExtraOpening:
                    extraOpenings.Add(new SupplierInterval(window.StartUtc, window.EndUtc));
                    break;
                case SupplierBusyWindowKind.External:
                    occupancies.Add(SupplierOccupancy.External(window.StartUtc, window.EndUtc));
                    break;
                default:
                    occupancies.Add(SupplierOccupancy.Block(window.StartUtc, window.EndUtc));
                    break;
            }
        }

        // The requests of the supplier (SP-04): one with hours is a TimedRequest, which no slot may overlap (buffer included);
        // one that only has a day (the check-out day of its stay, the time still to agree) counts for the day's maximum and
        // takes no hour. A day more on each side, as for the windows. The request being moved is left out: it must not be in
        // its own way. The showcase booking will add its holds here.
        var agendaRequests = await requests.ListForAgendaAsync(supplierOrgId, from.AddDays(-1), to.AddDays(1), cancellationToken);
        occupancies.AddRange(agendaRequests
            .Where(request => request.Id != exceptRequestId && JobStatuses.Contains(request.Status))
            .Select(request => request is { StartUtc: { } start, EndUtc: { } end }
                ? SupplierOccupancy.TimedRequest(start, end)
                : SupplierOccupancy.DatedRequest(request.Date)));

        return new SupplierPlanningInput(
            _clock.GetUtcNow().UtcDateTime,
            rules,
            ToBands(hours),
            timeOff.Select(entry => new SupplierDateRange(entry.FromDate, entry.ToDate)).ToList(),
            closedDays.Select(day => day.Date).ToHashSet(),
            extraOpenings,
            occupancies);
    }

    public Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        SupplierSlotQuery query,
        CancellationToken cancellationToken = default) =>
        PlanAsync(supplierOrgId, from, to, query, exceptRequestId: null, cancellationToken);

    public async Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        SupplierSlotQuery query,
        Guid? exceptRequestId,
        CancellationToken cancellationToken = default)
    {
        var input = await BuildPlanningInputAsync(supplierOrgId, from, to, exceptRequestId, cancellationToken);
        return SupplierSlotPlanner.PlanRange(from, to, input, query);
    }

    // ─── Queries: the only way these tables are read or changed ──────────────────

    private IQueryable<SupplierWorkingHours> HoursOf(Guid orgId) => HoursOf(db, orgId);

    private IQueryable<SupplierTimeOff> TimeOffOf(Guid orgId) => TimeOffOf(db, orgId);

    private IQueryable<SupplierBusyWindow> WindowsOf(Guid orgId) => WindowsOf(db, orgId);

    private IQueryable<SupplierBusyWindow> ManualWindowsOf(Guid orgId) => ManualWindowsOf(db, orgId);

    private IQueryable<SupplierSettings> SettingsOf(Guid orgId) => SettingsOf(db, orgId);

    // The static forms are internal so that a test can read the SQL they become on the PostgreSQL provider, without a server.
    // The tables are not tenant-filtered: the supplier org is always an explicit predicate.

    /// <summary>The weekly bands of <paramref name="orgId"/>.</summary>
    internal static IQueryable<SupplierWorkingHours> HoursOf(AppDbContext db, Guid orgId) =>
        db.SupplierWorkingHours.Where(h => h.OrgId == orgId);

    /// <summary>The time off of <paramref name="orgId"/>.</summary>
    internal static IQueryable<SupplierTimeOff> TimeOffOf(AppDbContext db, Guid orgId) =>
        db.SupplierTimeOff.Where(t => t.OrgId == orgId);

    /// <summary>The windows of <paramref name="orgId"/>: manual and of the calendar feed, of every kind.</summary>
    internal static IQueryable<SupplierBusyWindow> WindowsOf(AppDbContext db, Guid orgId) =>
        db.SupplierBusyWindows.Where(w => w.OrgId == orgId);

    /// <summary>The blocks and extra openings the supplier set by hand: what the console API lists, adds and deletes.</summary>
    internal static IQueryable<SupplierBusyWindow> ManualWindowsOf(AppDbContext db, Guid orgId) =>
        WindowsOf(db, orgId).Where(w => w.Source == SupplierBusyWindowSource.Manual && w.Kind != SupplierBusyWindowKind.External);

    /// <summary>The settings row of <paramref name="orgId"/> (at most one: the org is its key).</summary>
    internal static IQueryable<SupplierSettings> SettingsOf(AppDbContext db, Guid orgId) =>
        db.SupplierSettings.Where(s => s.OrgId == orgId);

    private async Task<List<SupplierAvailability>> ClosedDaysOfAsync(
        Guid orgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        await db.SupplierAvailability
            .AsNoTracking()
            .Where(a => a.OrgId == orgId && !a.Available && a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date)
            .ToListAsync(cancellationToken);

    private async Task<List<SupplierTimeOff>> TimeOffOverlappingAsync(
        Guid orgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        await TimeOffOf(orgId)
            .AsNoTracking()
            .Where(t => t.FromDate <= to && t.ToDate >= from)
            .OrderBy(t => t.FromDate)
            .ThenBy(t => t.ToDate)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

    private async Task<List<SupplierBusyWindow>> WindowsOverlappingAsync(
        Guid orgId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken) =>
        await WindowsOf(orgId)
            .AsNoTracking()
            .Where(w => w.StartUtc < endUtc && w.EndUtc > startUtc)
            .OrderBy(w => w.StartUtc)
            .ThenBy(w => w.EndUtc)
            .ThenBy(w => w.Id)
            .ToListAsync(cancellationToken);

    /// <summary>The settings row tracked for change, created (not yet saved) when the supplier has none: read after the lock was taken.</summary>
    private async Task<SupplierSettings> EnsureSettingsAsync(Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        var settings = await SettingsOf(orgId).FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
            return settings;

        settings = new SupplierSettings { OrgId = orgId, CreatedAt = now, UpdatedAt = now };
        db.SupplierSettings.Add(settings);
        return settings;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private Task<IDbContextTransaction?> LockAgendaAsync(Guid orgId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, CalendarSyncService.AvailabilityLock(orgId));

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private DateOnly Today() => _clock.TodayInRomeAsDateOnly();

    private static void EnsureRange(DateOnly from, DateOnly to, int maxDays)
    {
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "The last day cannot be before the first.");
        if (to.DayNumber - from.DayNumber + 1 > maxDays)
            throw new ArgumentOutOfRangeException(nameof(to), $"The range covers at most {maxDays} days.");
    }

    /// <summary>The bands as planner input: Monday first, then by start.</summary>
    private static List<SupplierWeeklyBand> ToBands(IEnumerable<SupplierWorkingHours> rows) =>
        rows
            .OrderBy(row => SupplierAgendaRules.MondayFirst(row.Weekday))
            .ThenBy(row => row.StartMinute)
            .Select(row => new SupplierWeeklyBand(row.Weekday, row.StartMinute, row.EndMinute))
            .ToList();

    private static SupplierPlanningRules ToRules(SupplierSettings settings) =>
        new(
            settings.BufferMinutes,
            settings.MaxJobsPerDay,
            settings.MinNoticeHours,
            settings.HorizonDays,
            settings.SlotStepMinutes,
            settings.ParallelJobs);
}
