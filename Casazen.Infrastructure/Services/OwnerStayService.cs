using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The owner calendar and the governed owner stays (E07/F07/US01/T01, NF-111). See <see cref="IOwnerStayService"/>.
/// </summary>
/// <remarks>
/// <para><b>No tenant of the caller.</b> The person of the owner portal has no org: every read and write here ignores the tenant filter of the request and
/// names the org, the owner profile and the property explicitly, after the <see cref="OwnerPortalAccess"/> of the caller said it reaches them. The agency
/// methods take the org the web layer resolved and filter on it the same way.</para>
/// <para><b>One lock per property</b> (<c>PostgresAdvisoryLocks.Scope.OwnerStays</c>): a request, a decision, a withdrawal and the work of the
/// reconciliation on the stays of a property are taken one at a time; the dates lock of the property is taken inside it by the block.</para>
/// <para><b>The block is written where a tenant exists.</b> The calendar services read the property through the tenant filter, so the block of a stay is
/// created or removed by the agency (its request has the org) or by the background work (no filter), never in the request of the owner: a request whose
/// rule asks for no person, and the withdrawal of an approved stay, are finished by <see cref="OwnerPortalOperationsJob"/> right after the save.</para>
/// </remarks>
public sealed partial class OwnerStayService(
    AppDbContext db,
    ICalendarBlockService calendarBlocks,
    ICalendarChangeRunner changeRunner,
    IInternalActivityService activities,
    IActivityLog activityLog,
    TimeProvider timeProvider,
    ILogger<OwnerStayService> logger,
    IBackgroundJobClient? jobs = null) : IOwnerStayService
{
    /// <summary>The entity type of the «To do» about an owner stay.</summary>
    public const string StayEntityType = "OwnerStay";

    // ---------------------------------------------------------------- owner portal

    public async Task<OwnerCalendar> GetCalendarAsync(
        OwnerPortalAccess access,
        Guid propertyId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var grant = access.RequireOn(propertyId, OwnerPortalPermission.CalendarRead);
        var grants = access.On(propertyId, OwnerPortalPermission.CalendarRead);

        var first = from ?? Today();
        var last = to ?? first.AddDays(OwnerStayRules.DefaultCalendarDays - 1);
        if (last < first || last.DayNumber - first.DayNumber + 1 > OwnerStayRules.MaxCalendarDays)
            throw new DomainRuleException(OwnerStayErrors.CalendarRangeInvalid, OwnerStayErrors.CalendarRangeInvalidMessageKey, OwnerStayRules.MaxCalendarDays);

        // Only the days of the ownerships in force of the caller (AC-NF111-1): never the occupancy of the time before the owner owned the property.
        var (ownedFrom, ownedUntil) = OwnedPeriod(grants);
        var shownFrom = first < ownedFrom ? ownedFrom : first;
        var shownTo = ownedUntil is { } lastOwned && last > lastOwned ? lastOwned : last;
        if (shownTo < shownFrom)
            return new OwnerCalendar(propertyId, first, last, []);

        var start = shownFrom.ToDateTime(TimeOnly.MinValue);
        var end = shownTo.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // Dates only: no column of a guest, of a channel or of a note is read (ADR-011-R3).
        var bookings = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == grant.OrgId)
            .Where(PropertyOccupancy.BookingTakesNightIn(propertyId, start, end))
            .Where(CheckoutHolds.OccupiesDates(null))
            .Select(b => new { b.CheckInDate, b.CheckOutDate })
            .ToListAsync(cancellationToken);
        var blocks = await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == grant.OrgId)
            .Where(PropertyOccupancy.BlockTakesNightIn(propertyId, start, end))
            .Select(b => new { b.StartUtc, b.EndUtc })
            .ToListAsync(cancellationToken);
        var stays = await Stays()
            .AsNoTracking()
            .Where(s => s.OrgId == grant.OrgId && s.PropertyId == propertyId)
            .Where(s => s.Status == OwnerStayStatus.Requested || s.Status == OwnerStayStatus.Approved)
            .Where(s => s.StartDate <= shownTo && s.EndDate > shownFrom)
            .ToListAsync(cancellationToken);
        var processes = await ProcessesAsync(stays.Select(s => s.BlockChangeProcessId), cancellationToken);

        var nights = new Dictionary<DateOnly, OwnerCalendarRange>();
        void Mark(DateTime takenFrom, DateTime takenUntil, string kind, Guid? stayId = null, string? stayState = null)
        {
            foreach (var night in PropertyOccupancy.NightsIn(takenFrom, takenUntil, start, end))
            {
                var day = DateOnly.FromDateTime(night);
                nights.TryAdd(day, new OwnerCalendarRange(day, day.AddDays(1), kind, stayId, stayState));
            }
        }

        // The precedence: the stays of the caller's owner profiles, then the guests, then whatever else closes the nights (a block, a co-owner's stay).
        var ownProfiles = grants.Select(g => g.PropertyOwnerId).ToHashSet();
        foreach (var stay in stays.Where(s => ownProfiles.Contains(s.PropertyOwnerId)))
        {
            var state = OwnerStayRules.StateOf(stay.Status, BlockSync(stay, processes));
            Mark(stay.StartDate.ToDateTime(TimeOnly.MinValue), stay.EndDate.ToDateTime(TimeOnly.MinValue), OwnerCalendarKinds.OwnerStay, stay.Id, state);
        }

        foreach (var booking in bookings)
            Mark(booking.CheckInDate, booking.CheckOutDate, OwnerCalendarKinds.Occupied);
        foreach (var block in blocks)
            Mark(block.StartUtc, block.EndUtc, OwnerCalendarKinds.Blocked);
        foreach (var stay in stays.Where(s => !ownProfiles.Contains(s.PropertyOwnerId)))
            Mark(stay.StartDate.ToDateTime(TimeOnly.MinValue), stay.EndDate.ToDateTime(TimeOnly.MinValue), OwnerCalendarKinds.Blocked);

        var days = new List<OwnerCalendarRange>();
        for (var day = shownFrom; day <= shownTo; day = day.AddDays(1))
            days.Add(nights.TryGetValue(day, out var taken) ? taken : new OwnerCalendarRange(day, day.AddDays(1), OwnerCalendarKinds.Available));

        return new OwnerCalendar(propertyId, shownFrom, shownTo, OwnerStayRules.Compress(days));
    }

    public async Task<IReadOnlyList<OwnerStayView>> ListForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var grants = access.Grants
            .Where(g => g.Permissions.HasFlag(OwnerPortalPermission.CalendarRead) || g.Permissions.HasFlag(OwnerPortalPermission.StaysRequest))
            .ToList();
        if (grants.Count == 0)
            throw new DomainForbiddenException(OwnerPortalErrors.PermissionMissing, OwnerPortalErrors.PermissionMissingMessageKey);

        var propertyIds = grants.Select(g => g.PropertyId).Distinct().ToList();
        var candidates = await Stays()
            .AsNoTracking()
            .Where(s => propertyIds.Contains(s.PropertyId))
            .OrderByDescending(s => s.StartDate)
            .Take(500)
            .ToListAsync(cancellationToken);

        // Only the stays of an owner profile the caller holds on that property now (an ended ownership closes them).
        var visible = candidates
            .Where(s => grants.Any(g => g.OrgId == s.OrgId && g.PropertyId == s.PropertyId && g.PropertyOwnerId == s.PropertyOwnerId))
            .ToList();
        return await ViewsAsync(visible, cancellationToken);
    }

    public async Task<OwnerStayRuleSummary> GetRuleForOwnerAsync(OwnerPortalAccess access, Guid propertyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var grant = access.RequireOn(propertyId, OwnerPortalPermission.StaysRequest);
        var rule = OwnerStayRules.EffectiveFor(propertyId, grant.PropertyOwnerId, await RulesOfAsync(grant.OrgId, propertyId, cancellationToken));
        var year = Today().Year;
        var held = await HeldNightsByYearAsync(grant.OrgId, propertyId, grant.PropertyOwnerId, null, cancellationToken);
        var used = held.GetValueOrDefault(year);
        int? remaining = rule.YearlyNightAllowance is { } allowance ? Math.Max(0, allowance - used) : null;
        return new OwnerStayRuleSummary(rule, year, used, remaining);
    }

    public async Task<OwnerStayView> RequestAsync(OwnerPortalAccess access, OwnerStayRequestCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(command);
        var grant = access.RequireOn(command.PropertyId, OwnerPortalPermission.StaysRequest);
        var today = Today();

        await using var transaction = await LockAsync(command.PropertyId, cancellationToken);

        var rule = OwnerStayRules.EffectiveFor(command.PropertyId, grant.PropertyOwnerId, await RulesOfAsync(grant.OrgId, command.PropertyId, cancellationToken));
        OwnerStayRules.EnsureObeys(today, command.StartDate, command.EndDate, rule);

        // The nights must be the owner's: inside an ownership in force of the owner profile on the property.
        var (ownedFrom, ownedUntil) = OwnedPeriod(access.On(command.PropertyId, OwnerPortalPermission.StaysRequest)
            .Where(g => g.PropertyOwnerId == grant.PropertyOwnerId && g.OrgId == grant.OrgId)
            .ToList());
        if (command.StartDate < ownedFrom || (ownedUntil is { } lastOwned && command.EndDate.AddDays(-1) > lastOwned))
            throw new DomainRuleException(OwnerStayErrors.OutsidePeriod, OwnerStayErrors.OutsidePeriodMessageKey);

        var held = await HeldNightsByYearAsync(grant.OrgId, command.PropertyId, grant.PropertyOwnerId, null, cancellationToken);
        OwnerStayRules.EnsureAllowance(rule.YearlyNightAllowance, held, OwnerStayRules.NightsByYear(command.StartDate, command.EndDate));
        await EnsureDatesFreeAsync(grant.OrgId, command.PropertyId, command.StartDate, command.EndDate, null, cancellationToken);

        var now = Now();
        var stay = new OwnerStayRequest
        {
            OrgId = grant.OrgId,
            PropertyId = command.PropertyId,
            PropertyOwnerId = grant.PropertyOwnerId,
            RequestedByOwnerUserId = access.OwnerUserId,
            RequestedByContactId = grant.ContactId,
            StartDate = command.StartDate,
            EndDate = command.EndDate,
            Nights = command.EndDate.DayNumber - command.StartDate.DayNumber,
            Status = OwnerStayStatus.Requested,
            AutoApprove = !rule.ApprovalRequired,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Through the DbSet: the Guid of the row is preset, so a row attached any other way would be tracked as modified.
        db.OwnerStayRequests.Add(stay);
        Audit(stay, access.Actor, OwnerPortalAuditStatuses.Requested);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Owner stay {StayId} requested on property {PropertyId}: {Nights} nights from {Start}, automatic approval {AutoApprove}",
            stay.Id, stay.PropertyId, stay.Nights, stay.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), stay.AutoApprove);

        // The rule asks for no person: the background work approves it (and blocks the dates) right away; the recurring run is the safety net.
        if (stay.AutoApprove)
            EnqueueProcess(stay.Id);

        return (await ViewsAsync([stay], cancellationToken))[0];
    }

    public async Task<OwnerStayView> CancelByOwnerAsync(OwnerPortalAccess access, Guid stayId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.Require(OwnerPortalPermission.StaysRequest);
        var head = await Stays().AsNoTracking().Where(s => s.Id == stayId).Select(s => new { s.OrgId, s.PropertyId, s.PropertyOwnerId }).FirstOrDefaultAsync(cancellationToken);

        // Another owner's stay, one of a property the caller no longer holds, one it may not request on, a missing one: the same 404 (ADR-011-R2).
        if (head is null || !access.On(head.PropertyId, OwnerPortalPermission.StaysRequest).Any(g => g.OrgId == head.OrgId && g.PropertyOwnerId == head.PropertyOwnerId))
            throw StayNotFound(stayId);

        await using var transaction = await LockAsync(head.PropertyId, cancellationToken);
        var stay = await Stays().FirstAsync(s => s.Id == stayId, cancellationToken);
        if (stay.Status == OwnerStayStatus.Cancelled)
            return (await ViewsAsync([stay], cancellationToken))[0];
        if (stay.Status == OwnerStayStatus.Rejected || stay.EndDate <= Today())
            throw new DomainConflictException(OwnerStayErrors.NotCancellable, OwnerStayErrors.NotCancellableMessageKey);

        var holdsBlock = stay.Status == OwnerStayStatus.Approved && stay.CalendarBlockId is not null && stay.BlockReleasedAt is null;
        var now = Now();
        stay.Status = OwnerStayStatus.Cancelled;
        stay.CancelledAt = now;
        stay.CancelledBy = access.Actor;
        stay.UpdatedAt = now;
        Audit(stay, access.Actor, OwnerPortalAuditStatuses.Withdrawn);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Owner stay {StayId} withdrawn by its owner; block to give back {HoldsBlock}", stay.Id, holdsBlock);

        // The block of an approved stay is given back by the background work (the calendar needs the org of the property): only that block.
        if (holdsBlock)
            EnqueueProcess(stay.Id);

        return (await ViewsAsync([stay], cancellationToken))[0];
    }

    // ---------------------------------------------------------------- shared

    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);

    private DateOnly Today() => timeProvider.TodayInRomeAsDateOnly();

    /// <summary>The stays of every org: the caller of the portal has none, the agency and the job name it in the predicate.</summary>
    private IQueryable<OwnerStayRequest> Stays() => db.OwnerStayRequests.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private Task<IDbContextTransaction?> LockAsync(Guid propertyId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, (PostgresAdvisoryLocks.Scope.OwnerStays, propertyId.ToString("N")));

    /// <summary>The first and the last night (Europe/Rome) the grants cover: from the earliest start to the latest end, open when one has no end.</summary>
    private static (DateOnly From, DateOnly? Until) OwnedPeriod(IReadOnlyCollection<OwnerPortalGrant> grants)
    {
        if (grants.Count == 0)
            return (DateOnly.MaxValue, DateOnly.MinValue);

        var from = grants.Min(g => RomeCalendar.DateInRome(g.OwnershipStart));
        DateOnly? until = grants.Any(g => g.OwnershipEnd is null) ? null : grants.Max(g => RomeCalendar.DateInRome(g.OwnershipEnd!.Value));
        return (from, until);
    }

    private async Task<List<OwnerStayRule>> RulesOfAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken) =>
        await db.OwnerStayRules
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.PropertyId == propertyId)
            .ToListAsync(cancellationToken);

    /// <summary>The nights the owner profile holds on the property (requested and approved stays) in each calendar year, the stay <paramref name="exceptStayId"/> left out.</summary>
    private async Task<IReadOnlyDictionary<int, int>> HeldNightsByYearAsync(
        Guid orgId,
        Guid propertyId,
        Guid propertyOwnerId,
        Guid? exceptStayId,
        CancellationToken cancellationToken)
    {
        var stays = await Stays()
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.PropertyId == propertyId && s.PropertyOwnerId == propertyOwnerId && s.Id != exceptStayId)
            .Where(s => s.Status == OwnerStayStatus.Requested || s.Status == OwnerStayStatus.Approved)
            .Select(s => new { s.StartDate, s.EndDate })
            .ToListAsync(cancellationToken);

        var byYear = new Dictionary<int, int>();
        foreach (var stay in stays)
        {
            foreach (var (year, nights) in OwnerStayRules.NightsByYear(stay.StartDate, stay.EndDate))
                byYear[year] = byYear.GetValueOrDefault(year) + nights;
        }

        return byYear;
    }

    /// <summary>
    /// Refuses dates a booking (the one rule of the nights taken, <see cref="PropertyOccupancy"/> and <see cref="CheckoutHolds.OccupiesDates"/>), a block or
    /// another owner stay of the property (a co-owner's included, requested or approved) takes. The block of the stay itself is left out.
    /// </summary>
    private async Task EnsureDatesFreeAsync(Guid orgId, Guid propertyId, DateOnly startDate, DateOnly endDate, Guid? exceptStayId, CancellationToken cancellationToken)
    {
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var end = endDate.ToDateTime(TimeOnly.MinValue);
        var takenByBooking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => b.OrgId == orgId)
            .Where(PropertyOccupancy.BookingTakesNightIn(propertyId, start, end))
            .Where(CheckoutHolds.OccupiesDates(null))
            .AnyAsync(cancellationToken);
        var takenByBlock = !takenByBooking && await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => b.OrgId == orgId && b.Id != exceptStayId)
            .Where(PropertyOccupancy.BlockTakesNightIn(propertyId, start, end))
            .AnyAsync(cancellationToken);
        var takenByStay = !takenByBooking && !takenByBlock && await Stays()
            .Where(s => s.OrgId == orgId && s.PropertyId == propertyId && s.Id != exceptStayId)
            .Where(s => s.Status == OwnerStayStatus.Requested || s.Status == OwnerStayStatus.Approved)
            .AnyAsync(s => s.StartDate < endDate && s.EndDate > startDate, cancellationToken);

        if (takenByBooking || takenByBlock || takenByStay)
            throw new DomainConflictException(OwnerStayErrors.DatesUnavailable, OwnerStayErrors.DatesUnavailableMessageKey);
    }

    /// <summary>The state and the stable code of the calendar changes named, by id.</summary>
    private async Task<Dictionary<Guid, (CalendarChangeState State, string? ErrorCode)>> ProcessesAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (wanted.Count == 0)
            return new Dictionary<Guid, (CalendarChangeState State, string? ErrorCode)>();

        var rows = await db.CalendarChangeProcesses
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .Select(p => new { p.Id, p.State, p.ErrorCode })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(p => p.Id, p => (p.State, p.ErrorCode));
    }

    /// <summary>The live state of the block of a stay: none without a block; acknowledged when no change was recorded for it.</summary>
    private static OwnerStaySyncState BlockSync(OwnerStayRequest stay, IReadOnlyDictionary<Guid, (CalendarChangeState State, string? ErrorCode)> processes) =>
        stay.CalendarBlockId is null ? OwnerStaySyncState.None : SyncOf(stay.BlockChangeProcessId, processes);

    /// <summary>The live state of the release of the block of a stay: pending while a withdrawn stay still holds it.</summary>
    private static OwnerStaySyncState ReleaseSync(OwnerStayRequest stay, IReadOnlyDictionary<Guid, (CalendarChangeState State, string? ErrorCode)> processes)
    {
        if (stay.BlockReleasedAt is not null)
            return SyncOf(stay.ReleaseChangeProcessId, processes);

        return stay.Status == OwnerStayStatus.Cancelled && stay.CalendarBlockId is not null ? OwnerStaySyncState.Pending : OwnerStaySyncState.None;
    }

    private static OwnerStaySyncState SyncOf(Guid? processId, IReadOnlyDictionary<Guid, (CalendarChangeState State, string? ErrorCode)> processes) =>
        processId is not { } id
            ? OwnerStayRules.SyncOf(false, null)
            : OwnerStayRules.SyncOf(true, processes.TryGetValue(id, out var process) ? process.State : null);

    private static string? ErrorOf(Guid? processId, IReadOnlyDictionary<Guid, (CalendarChangeState State, string? ErrorCode)> processes) =>
        processId is { } id && processes.TryGetValue(id, out var process) ? process.ErrorCode : null;

    /// <summary>The stays as the portal and the console read them, with the live state of their calendar changes.</summary>
    private async Task<List<OwnerStayView>> ViewsAsync(IReadOnlyCollection<OwnerStayRequest> stays, CancellationToken cancellationToken)
    {
        var processes = await ProcessesAsync(stays.SelectMany(s => new[] { s.BlockChangeProcessId, s.ReleaseChangeProcessId }), cancellationToken);
        var propertyIds = stays.Select(s => s.PropertyId).Distinct().ToList();
        var names = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => propertyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        return stays.Select(stay =>
        {
            var block = BlockSync(stay, processes);
            var release = ReleaseSync(stay, processes);
            var error = block == OwnerStaySyncState.Failed
                ? ErrorOf(stay.BlockChangeProcessId, processes) ?? stay.SyncErrorCode
                : release == OwnerStaySyncState.Failed ? ErrorOf(stay.ReleaseChangeProcessId, processes) ?? stay.SyncErrorCode : null;
            return new OwnerStayView(
                stay.Id,
                stay.OrgId,
                stay.PropertyId,
                names.GetValueOrDefault(stay.PropertyId, string.Empty),
                stay.PropertyOwnerId,
                stay.StartDate,
                stay.EndDate,
                stay.Nights,
                stay.Status,
                OwnerStayRules.StateOf(stay.Status, block),
                block,
                release,
                error,
                stay.CreatedAt,
                stay.DecidedAt,
                stay.CancelledAt,
                stay.RequestedByContactId is not null);
        }).ToList();
    }

    /// <summary>Stages the line of the activity log of a change of a stay, in the unit of work of the change. Codes only.</summary>
    private void Audit(OwnerStayRequest stay, string? actor, string status) =>
        activityLog.Record(OrgActivity.Of(
            stay.OrgId,
            OrgActivityType.OwnerPortalChanged,
            actor,
            stay.Id.ToString(),
            (OrgActivityDetailKeys.Part, OwnerPortalParts.Stay),
            (OrgActivityDetailKeys.Status, status)));

    /// <summary>Asks the background work to finish one stay now (an automatic approval, a block to give back); a queue that cannot take it is not a failure.</summary>
    private void EnqueueProcess(Guid stayId)
    {
        if (jobs is null)
            return;

        try
        {
            jobs.Enqueue<OwnerPortalOperationsJob>(job => job.ProcessStayAsync(stayId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Owner stay {StayId} could not be enqueued: the recurring run finishes it", stayId);
        }
    }

    private static NotFoundException StayNotFound(Guid stayId) =>
        new($"Owner stay {stayId} not found")
        {
            Code = OwnerStayErrors.NotFound,
            MessageKey = OwnerStayErrors.NotFoundMessageKey,
        };
}
