using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// The backup suppliers of a request (SP-BKP): the group a request and its backups form, the pre-approval of a backup, the take of a job
// that belongs to a group, and the pass that follows a group to its end (the recurring job and the calls right after a refusal or a
// cancellation). The rules are ServiceRequestGroupRules'; this is the part that reads and writes.
//
// The discipline, in short: every change of a group runs under its advisory lock (taken BEFORE the supplier's calendar lock, never after),
// reads the group and its requests again after taking it (a tracked entity keeps the values it was read with, so the tracker is cleared),
// and saves the rows it touches in ONE SaveChanges, so the job is given to exactly one supplier and the others are closed with it or not
// at all. Notifications go out after the commit, by the winner only.
public partial class ServiceRequestService
{
    /// <summary>The user recorded as having taken a job when a backup that accepted gets it and no member is known (never in practice).</summary>
    private const string GroupSystemUser = "system";

    // ─── Creation ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The backup suppliers of a new request, none when the rule is off (the flag <c>SupplierRequestBackups</c>, the org's choice, the request's
    /// own) or nobody else covers the comune at that time. A failure to choose never stops the request: it goes to the chosen supplier alone.
    /// </summary>
    private async Task<IReadOnlyList<SupplierProfile>> SelectBackupsAsync(
        CreateServiceRequestCommand command,
        Property property,
        string category,
        RequestSchedule schedule,
        CancellationToken cancellationToken)
    {
        if (backupSelector is null
            || featureFlags?.IsEnabled(FeatureFlags.SupplierRequestBackups) != true
            || !(command.UseBackupSuppliers ?? property.Org.SupplierBackupsEnabled))
        {
            return [];
        }

        try
        {
            return await backupSelector.SelectAsync(
                property,
                category,
                command.Urgency,
                command.OrgId,
                command.SupplierOrgId,
                schedule.Start,
                schedule.End,
                slotPlanned: command.DurationMinutes is null,
                options.Value.MaxBackupSuppliers,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The backup suppliers of a new request could not be chosen: it goes to the chosen supplier alone");
            return [];
        }
    }

    /// <summary>
    /// Ties the new <paramref name="primary"/> to a group and adds the group and one request per backup to the context, to be saved with it in
    /// one go. A backup request has the same data as the primary's (host, property, lease, stay, category, notes, time, price) and is waiting
    /// (<c>Richiesto</c>); it has no catalog service of its own (the primary's service is another supplier's), only the name the host saw.
    /// </summary>
    private IReadOnlyList<(ServiceRequest Request, SupplierProfile Supplier)> AttachBackupGroup(
        ServiceRequest primary,
        IReadOnlyList<SupplierProfile> suppliers,
        bool slotPlanned,
        DateTime now)
    {
        var group = new ServiceRequestGroup
        {
            OrgId = primary.OrgId,
            Phase = ServiceRequestGroupPhase.PrimaryWindow,
            PrimaryDueAt = now.AddMinutes(options.Value.PrimaryResponseMinutes),
            SlotPlanned = slotPlanned,
            CreatedAt = now,
        };

        primary.GroupId = group.Id;
        primary.GroupRole = ServiceRequestGroupRole.Primary;
        primary.ResponseDueAt = group.PrimaryDueAt;

        var backups = suppliers
            .Select(supplier => (
                Request: new ServiceRequest
                {
                    OrgId = primary.OrgId,
                    BookingId = primary.BookingId,
                    RentalContext = primary.RentalContext,
                    PropertyId = primary.PropertyId,
                    LeaseContractId = primary.LeaseContractId,
                    SupplierOrgId = supplier.OrgId,
                    Category = primary.Category,
                    Urgency = primary.Urgency,
                    Notes = primary.Notes,
                    ChargeToGuest = primary.ChargeToGuest,
                    Status = ServiceRequestStatus.Richiesto,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ServiceNameSnapshot = primary.ServiceNameSnapshot,
                    EstimatedAmountCents = primary.EstimatedAmountCents,
                    ScheduledStartUtc = primary.ScheduledStartUtc,
                    ScheduledEndUtc = primary.ScheduledEndUtc,
                    ResponseDueAt = group.PrimaryDueAt,
                    GroupId = group.Id,
                    GroupRole = ServiceRequestGroupRole.Backup,
                },
                Supplier: supplier))
            .ToList();

        db.ServiceRequestGroups.Add(group);
        db.ServiceRequests.AddRange(backups.Select(backup => backup.Request));
        return backups;
    }

    // ─── Locks and reads ─────────────────────────────────────────────────────────────────────────────────────────────

    private static (PostgresAdvisoryLocks.Scope Scope, string Key) GroupLock(Guid groupId) =>
        (PostgresAdvisoryLocks.Scope.ServiceRequestGroup, groupId.ToString("N"));

    /// <summary>
    /// The supplier's calendar lock, preceded by the lock of the request's group when it has one: always in this order, so a take and the
    /// pass that promotes a backup never wait for each other.
    /// </summary>
    private Task<IDbContextTransaction?> LockSupplierCalendarAsync(Guid supplierOrgId, Guid? groupId, CancellationToken cancellationToken) =>
        groupId is { } id
            ? PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, GroupLock(id), CalendarSyncService.AvailabilityLock(supplierOrgId))
            : LockSupplierCalendarAsync(supplierOrgId, cancellationToken);

    /// <summary>A group and its requests, as committed when they were read (<see cref="LoadGroupAsync"/>).</summary>
    private sealed class GroupState(ServiceRequestGroup group, IReadOnlyList<ServiceRequest> members)
    {
        public ServiceRequestGroup Group { get; } = group;

        public IReadOnlyList<ServiceRequest> Members { get; } = members;

        /// <summary>The request the host follows: the primary, or the backup that replaced it. Null only for a group in a state that cannot be.</summary>
        public ServiceRequest? Head => Members.FirstOrDefault(m => m.GroupRole is ServiceRequestGroupRole.Primary or ServiceRequestGroupRole.PromotedBackup);

        public IEnumerable<ServiceRequest> Backups => Members.Where(m => m.GroupRole == ServiceRequestGroupRole.Backup);

        public ServiceRequest? Find(Guid requestId) => Members.FirstOrDefault(m => m.Id == requestId);
    }

    /// <summary>
    /// The group and its requests as they are committed now. The tracker is cleared first: a tracked entity keeps the values it was read with,
    /// and a group is judged on what the last holder of its lock committed. Callers have nothing pending to save, and use the instances returned
    /// here from then on. The group table has no tenant filter (TN-2 allow-list): the id is the predicate, and the caller holds the group's lock or
    /// reached the id through a request it is allowed to act on.
    /// </summary>
    private async Task<GroupState?> LoadGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        var group = await db.ServiceRequestGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
            return null;

        var members = await db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .Where(r => r.GroupId == groupId)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        return new GroupState(group, members);
    }

    private static ServiceRequestGroupRules.Member MemberOf(ServiceRequest request) => new(
        request.Id,
        request.GroupRole,
        request.Status,
        request.BackupAcceptedAt,
        HasPendingProposal: HasPendingOffer(request),
        request.CancelledBy);

    /// <summary>
    /// The supplier made the host an offer that waits for its answer: a time proposed (SP-04) or a quote (SP-04b). The supplier did answer, so
    /// the host is the one that is waited for and the group does not move.
    /// </summary>
    private static bool HasPendingOffer(ServiceRequest request) => request.ProposedStartUtc is not null || request.QuoteSentAt is not null;

    /// <summary>Closes a request of a group on behalf of CasaZen: <c>Richiesto → Annullato</c> with a code the client translates.</summary>
    private static void CloseBySystem(ServiceRequest request, string reason, DateTime now)
    {
        request.Status = ServiceRequestStatus.Annullato;
        request.CancelledAt = now;
        request.CancelledBy = ServiceRequestActorParty.System;
        request.CancellationReason = reason;
        request.ResponseDueAt = null;

        // The database refuses an offer on a request that is not new: the primary closed in the second window may have one waiting.
        ClearProposal(request);
        ClearQuote(request);
        request.UpdatedAt = now;
    }

    // ─── The take of a request of a group ────────────────────────────────────────────────────────────────────────────

    /// <summary>The group a request is taken in, and the request as committed now (<see cref="RequireGroupTakeAsync"/>).</summary>
    private sealed record GroupTake(GroupState State, ServiceRequest Request);

    /// <summary>
    /// What closing a group after a take changed, to tell the people it concerns after the commit. <paramref name="PrimaryDueAt"/> is the end
    /// of the primary's window, which the primary that was replaced did not meet.
    /// </summary>
    private sealed record GroupClosure(
        bool WinnerIsBackup,
        ServiceRequest? ReplacedPrimary,
        IReadOnlyList<(ServiceRequest Request, ServiceRequestBackupClosedKind Kind)> ClosedBackups,
        DateTime PrimaryDueAt);

    // ─── Race closed, deadlines of the replies ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 409 <see cref="ServiceRequestGroupErrorCodes.BackupRaceClosed"/> for a backup whose request the race closed: another backup accepted
    /// first, so this one can no longer accept nor take it. Clearer for the supplier than "the request changed", and it loses nothing by it.
    /// </summary>
    private static void ThrowIfRaceClosed(ServiceRequest request)
    {
        if (request is
            {
                GroupRole: ServiceRequestGroupRole.Backup,
                Status: ServiceRequestStatus.Annullato,
                CancellationReason: ServiceRequestCancellationReasons.BackupRaceClosed,
            })
        {
            throw new DomainConflictException(
                ServiceRequestGroupErrorCodes.BackupRaceClosed, ServiceRequestGroupErrorCodes.BackupRaceClosedMessageKey);
        }
    }

    /// <summary>
    /// The deadline a reply of the supplier is judged against for its reliability (SP-REL): the <c>ResponseDueAt</c> the request has, except
    /// for the primary of a group, whose deadline is the end of its own window whatever the second window says (an answer in the second
    /// window is late for the primary). Read before the answer, which clears <c>ResponseDueAt</c>.
    /// </summary>
    private async Task<DateTime?> ReplyDeadlineAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request is not { GroupRole: ServiceRequestGroupRole.Primary, GroupId: { } groupId })
            return request.ResponseDueAt;

        // The group has no tenant filter (TN-2 allow-list): the id is the predicate.
        var group = await db.ServiceRequestGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        return group?.PrimaryDueAt ?? request.ResponseDueAt;
    }

    /// <summary>When the primary began to wait for the group's first window to end: the creation, or the start of the last window when the host's answer to a proposal restarted it.</summary>
    private DateTime PrimaryWindowStart(ServiceRequestGroup group, ServiceRequest primary)
    {
        var start = group.PrimaryDueAt.AddMinutes(-options.Value.PrimaryResponseMinutes);
        return start > primary.CreatedAt ? start : primary.CreatedAt;
    }

    /// <summary>
    /// A supplier of the group did not answer in its window (SP-REL): it loses the points of a missed deadline, a backup only the small
    /// share, unless it could not have answered (not active, on time off, closed while the request waited). True when it did lose them, so
    /// the e-mail it gets can say why. Written after the group's change is committed, never inside it.
    /// </summary>
    private async Task<bool> RecordMissedWindowAsync(
        ServiceRequest request,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken)
    {
        if (reliability is null || !CountsForReliability(request))
            return false;

        return await reliability.RecordNoReplyAsync(
            request.SupplierOrgId,
            request.Id,
            windowStartUtc,
            windowEndUtc,
            asBackup: request.GroupRole == ServiceRequestGroupRole.Backup,
            cancellationToken);
    }

    /// <summary>
    /// Under the group's lock: reads the group again and refuses the take unless <see cref="ServiceRequestGroupRules.CanTake"/> allows it.
    /// 409 <see cref="ServiceRequestErrorCodes.StateChanged"/> when the group is over (someone else has the job); 422 with the code of the
    /// rule otherwise.
    /// </summary>
    private async Task<GroupTake> RequireGroupTakeAsync(Guid requestId, Guid groupId, CancellationToken cancellationToken)
    {
        var state = await LoadGroupAsync(groupId, cancellationToken) ?? throw RequestNotFound(requestId);
        var request = state.Find(requestId) ?? throw RequestNotFound(requestId);
        var head = state.Head;
        if (state.Group.Phase == ServiceRequestGroupPhase.Closed || head is null)
        {
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }

        // The request as committed now: the caller checked a copy read before the lock, and the group may have closed it meanwhile (a
        // backup that lost the race says so plainly).
        ThrowIfRaceClosed(request);
        if (request.Status != ServiceRequestStatus.Richiesto)
        {
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }

        var verdict = ServiceRequestGroupRules.CanTake(
            state.Group.Phase,
            state.Group.PrimaryDueAt,
            Now(),
            MemberOf(request),
            MemberOf(head),
            [.. state.Backups.Select(MemberOf)]);
        switch (verdict)
        {
            case ServiceRequestGroupRules.TakeVerdict.Allowed:
                return new GroupTake(state, request);
            case ServiceRequestGroupRules.TakeVerdict.GroupClosed:
                throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
            case ServiceRequestGroupRules.TakeVerdict.BackupMustWaitForPrimary:
                throw new DomainRuleException(
                    ServiceRequestGroupErrorCodes.BackupWaitsForPrimary, ServiceRequestGroupErrorCodes.BackupWaitsForPrimaryMessageKey);
            case ServiceRequestGroupRules.TakeVerdict.PrimaryWindowPassed:
                throw new DomainRuleException(
                    ServiceRequestGroupErrorCodes.PrimaryWindowPassed, ServiceRequestGroupErrorCodes.PrimaryWindowPassedMessageKey);
            default:
                throw new DomainRuleException(
                    ServiceRequestGroupErrorCodes.BackupNotYourTurn, ServiceRequestGroupErrorCodes.BackupNotYourTurnMessageKey);
        }
    }

    /// <summary>
    /// Closes the group around the request that is being taken: the group is over, the backups still waiting are closed (the primary took the
    /// job, or a backup did first), and when the winner is a backup it replaces the primary in the host's lists and the primary, which did not
    /// answer in time, is cancelled. Only <b>changes the tracked entities</b>: the caller saves them together with the take.
    /// </summary>
    private static GroupClosure CloseGroupAfterTake(GroupTake take, DateTime now)
    {
        var group = take.State.Group;
        var winner = take.Request;
        var head = take.State.Head!;
        var winnerIsBackup = winner.GroupRole == ServiceRequestGroupRole.Backup;

        group.Phase = ServiceRequestGroupPhase.Closed;
        group.ClosedAt = now;

        ServiceRequest? replaced = null;
        if (winnerIsBackup)
        {
            // The backups still waiting are the others: the winner is the one being taken.
            winner.GroupRole = ServiceRequestGroupRole.PromotedBackup;
            if (head.Status == ServiceRequestStatus.Richiesto)
            {
                CloseBySystem(head, ServiceRequestCancellationReasons.NoResponse, now);
                replaced = head;
            }

            head.GroupRole = ServiceRequestGroupRole.ReplacedPrimary;
        }

        var closed = new List<(ServiceRequest Request, ServiceRequestBackupClosedKind Kind)>();
        foreach (var backup in take.State.Backups.Where(b => b.Id != winner.Id && b.Status == ServiceRequestStatus.Richiesto).ToList())
        {
            // The primary took the job: a backup that had accepted is released (and told its time is free again), the others are told the job
            // is taken. Neither loses anything.
            var (reason, kind) = winnerIsBackup
                ? (ServiceRequestCancellationReasons.TakenByBackup, ServiceRequestBackupClosedKind.TakenByBackup)
                : (ServiceRequestCancellationReasons.PrimaryTookIt, PrimaryTookItKind(backup));
            CloseBySystem(backup, reason, now);
            closed.Add((backup, kind));
        }

        return new GroupClosure(winnerIsBackup, replaced, closed, group.PrimaryDueAt);
    }

    /// <summary>How a backup is told that the primary took the job: released (its time was held) when it had accepted, closed otherwise.</summary>
    private static ServiceRequestBackupClosedKind PrimaryTookItKind(ServiceRequest backup) =>
        backup.BackupAcceptedAt is not null ? ServiceRequestBackupClosedKind.ReleasedByPrimary : ServiceRequestBackupClosedKind.PrimaryTookIt;

    /// <summary>
    /// Tells who the closing of a group concerns, after the commit: the primary that lost its job (it did not take it in its window, so it
    /// loses the points of a missed deadline unless it could not have answered, and its e-mail says so) and the backups that were closed.
    /// </summary>
    private async Task NotifyClosureAsync(GroupClosure closure, CancellationToken cancellationToken)
    {
        if (closure.ReplacedPrimary is { } replaced)
        {
            var windowStart = closure.PrimaryDueAt.AddMinutes(-options.Value.PrimaryResponseMinutes);
            var lostPoints = await RecordMissedWindowAsync(
                replaced, windowStart > replaced.CreatedAt ? windowStart : replaced.CreatedAt, closure.PrimaryDueAt, cancellationToken);
            await notifier.NotifySupplierCancelledAsync(replaced, cancellationToken, lostPoints);
        }

        foreach (var (request, kind) in closure.ClosedBackups)
            await notifier.NotifyBackupClosedAsync(request, kind, cancellationToken);
    }

    // ─── The acceptance of a backup: the first one closes the race ──────────────────────────────────────────────────

    public async Task<ServiceRequest> AcceptAsBackupAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
        ThrowIfRaceClosed(request);
        if (request is not { GroupId: { } groupId, GroupRole: ServiceRequestGroupRole.Backup, Status: ServiceRequestStatus.Richiesto })
        {
            throw new DomainRuleException(
                ServiceRequestGroupErrorCodes.NotAWaitingBackup, ServiceRequestGroupErrorCodes.NotAWaitingBackupMessageKey);
        }

        var dueBeforeAnswer = request.ResponseDueAt;
        IReadOnlyList<ServiceRequest> closedByTheRace = [];

        // The group's lock first, then the supplier's calendar lock: the acceptance holds the supplier's slot, so it is judged under both,
        // like the time of any take.
        IDbContextTransaction? transaction = await LockSupplierCalendarAsync(supplierOrgId, groupId, cancellationToken);
        await using (transaction)
        {
            var state = await LoadGroupAsync(groupId, cancellationToken) ?? throw RequestNotFound(id);
            request = state.Find(id) ?? throw RequestNotFound(id);

            // Another backup accepted while this call waited for the lock: this request was closed with the race.
            ThrowIfRaceClosed(request);

            // Closed meanwhile (the primary took the job, someone else did, the host cancelled): nothing to accept anymore.
            if (state.Group.Phase == ServiceRequestGroupPhase.Closed || request.Status != ServiceRequestStatus.Richiesto)
            {
                throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
            }

            if (state.Group.Phase == ServiceRequestGroupPhase.BackupWindow)
            {
                throw new DomainRuleException(
                    ServiceRequestGroupErrorCodes.BackupAlreadyActive, ServiceRequestGroupErrorCodes.BackupAlreadyActiveMessageKey);
            }

            // Accepting twice is not an error and changes nothing.
            if (request.BackupAcceptedAt is not null)
                return request;

            // The first backup that accepts closes the race: if another one has accepted already this one is out of it (its request was closed
            // in the same save; this is the guard for rows that say otherwise).
            if (state.Backups.Any(b => b.Id != request.Id && b.Status == ServiceRequestStatus.Richiesto && b.BackupAcceptedAt is not null))
            {
                throw new DomainConflictException(
                    ServiceRequestGroupErrorCodes.BackupRaceClosed, ServiceRequestGroupErrorCodes.BackupRaceClosedMessageKey);
            }

            // It says it will take the job at that time, and from now on its agenda holds the time: the time must be free for it, judged here
            // under the supplier's lock like the time of any take (409 supplier_slot_unavailable), and again when the job is assigned.
            if (backupSelector is not null
                && request is { ScheduledStartUtc: { } start, ScheduledEndUtc: { } end }
                && !await backupSelector.IsFreeAsync(supplierOrgId, start, end, state.Group.SlotPlanned, cancellationToken))
            {
                throw new DomainConflictException(ServiceRequestErrorCodes.SlotUnavailable, ServiceRequestErrorCodes.SlotUnavailableMessageKey);
            }

            var now = Now();
            request.BackupAcceptedAt = now;
            request.BackupAcceptedByUserId = userId;
            request.UpdatedAt = now;

            // The race is closed for the others in the same save: either this supplier is the one, or nothing changes. Nobody loses anything.
            var others = state.Backups.Where(b => b.Id != request.Id && b.Status == ServiceRequestStatus.Richiesto).ToList();
            foreach (var other in others)
                CloseBySystem(other, ServiceRequestCancellationReasons.BackupRaceClosed, now);

            await SaveAsync(request, "backup acceptance", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            closedByTheRace = others;
        }

        logger.LogInformation(
            "ServiceRequest {Id}: the backup supplier {SupplierOrgId} accepted as a backup and closed the race for {Closed} other backups",
            id, supplierOrgId, closedByTheRace.Count);

        // After the commit, by the winner only: the answer counts for the supplier's reliability, the ones left behind are told.
        await RecordReplyAsync(request, dueBeforeAnswer, asBackup: true, cancellationToken);
        foreach (var other in closedByTheRace)
            await notifier.NotifyBackupClosedAsync(other, ServiceRequestBackupClosedKind.RaceClosed, cancellationToken);

        // The primary's hour may be over already (the job had not looked at the group yet): then the backup that accepted has the job at once.
        await ResolveGroupAfterChangeAsync(groupId, cancellationToken);
        return await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);
    }

    // ─── The pass over a group ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceRequestGroupResolution> ResolveGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var notices = new List<Func<Task>>();
        ServiceRequestGroupResolution outcome;
        try
        {
            IDbContextTransaction? transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, GroupLock(groupId));
            await using (transaction)
            {
                outcome = await ResolveLockedAsync(groupId, notices, cancellationToken);
                await CommitAsync(transaction, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException
            or DomainConflictException { Code: ServiceRequestErrorCodes.StateChanged })
        {
            // Another call got there first: nothing of this pass was saved, the next one looks again.
            db.ChangeTracker.Clear();
            logger.LogInformation("Group {GroupId} changed under the pass, left as it is", groupId);
            return ServiceRequestGroupResolution.Conflict;
        }

        foreach (var notice in notices)
        {
            try
            {
                await notice();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "A notification of group {GroupId} could not be queued", groupId);
            }
        }

        return outcome;
    }

    /// <summary>
    /// The pass, with the group's lock held: reads the group, decides from the rules and the clock what is due, does it in one save, and leaves
    /// the people to tell in <paramref name="notices"/> for after the commit.
    /// </summary>
    private async Task<ServiceRequestGroupResolution> ResolveLockedAsync(
        Guid groupId,
        List<Func<Task>> notices,
        CancellationToken cancellationToken)
    {
        var now = Now();
        var state = await LoadGroupAsync(groupId, cancellationToken);
        if (state is null || state.Group.Phase == ServiceRequestGroupPhase.Closed)
            return ServiceRequestGroupResolution.NothingToDo;

        var group = state.Group;
        var head = state.Head;
        if (head is null)
        {
            logger.LogWarning("Group {GroupId} has no request the host follows: left as it is", groupId);
            return ServiceRequestGroupResolution.NothingToDo;
        }

        // 1. The primary (or the backup that replaced it) took the job: the backups that still wait are closed.
        if (head.Status is ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso
            or ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato)
        {
            return await CloseWaitingBackupsAsync(
                state, ServiceRequestCancellationReasons.PrimaryTookIt, ServiceRequestBackupClosedKind.PrimaryTookIt,
                ServiceRequestGroupResolution.ClosedPrimaryTook, now, notices, cancellationToken);
        }

        // 2. The host cancelled the request: there is no job to take.
        if (head is { Status: ServiceRequestStatus.Annullato, CancelledBy: ServiceRequestActorParty.Host })
        {
            return await CloseWaitingBackupsAsync(
                state, ServiceRequestCancellationReasons.RequestCancelledByHost, ServiceRequestBackupClosedKind.RequestCancelledByHost,
                ServiceRequestGroupResolution.ClosedCancelled, now, notices, cancellationToken);
        }

        if (group.Phase == ServiceRequestGroupPhase.PrimaryWindow)
        {
            var primaryOut = ServiceRequestGroupRules.PrimaryIsOut(MemberOf(head), group.Phase, group.PrimaryDueAt, now);
            if (!primaryOut)
                return ServiceRequestGroupResolution.NothingToDo;

            // 3. The primary is out: the earliest backup that accepted takes the job, and the next one when its time is not free.
            foreach (var candidate in ServiceRequestGroupRules.PreApprovedInOrder(state.Backups.Select(MemberOf)))
            {
                var row = state.Find(candidate.RequestId)!;
                try
                {
                    var taken = await TakeAsync(
                        row.Id, row.SupplierOrgId, row.BackupAcceptedByUserId ?? GroupSystemUser, command: null, cancellationToken);
                    notices.Add(() => notifier.NotifyBackupWonAsync(taken, cancellationToken));
                    return ServiceRequestGroupResolution.ClosedBackupTook;
                }
                catch (Exception ex) when (IsBackupRefusal(ex))
                {
                    logger.LogInformation(
                        "ServiceRequest {Id}: the backup could not take the job when it was assigned to it ({Reason})",
                        row.Id, (ex as DomainException)?.Code);

                    // TakeAsync cleared the tracker: read the group again, and close the one that could not.
                    state = await LoadGroupAsync(groupId, cancellationToken) ?? throw new DomainConflictException(
                        ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
                    var dropped = state.Find(row.Id);
                    if (dropped is { Status: ServiceRequestStatus.Richiesto })
                    {
                        CloseBySystem(dropped, ServiceRequestCancellationReasons.BackupSlotUnavailable, now);
                        await SaveAsync(dropped, "backup dropped", cancellationToken);
                        notices.Add(() => notifier.NotifyBackupClosedAsync(dropped, ServiceRequestBackupClosedKind.SlotUnavailable, cancellationToken));
                    }
                }
            }

            // 4. Nobody had accepted (or none could take it): the second window opens.
            state = await LoadGroupAsync(groupId, cancellationToken) ?? throw new DomainConflictException(
                ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
            return await OpenBackupWindowAsync(state, now, notices, cancellationToken);
        }

        // The second window.
        var waiting = state.Backups.Where(b => b.Status == ServiceRequestStatus.Richiesto).ToList();
        var headLive = head.Status == ServiceRequestStatus.Richiesto;
        if (headLive && HasPendingOffer(head))
            return ServiceRequestGroupResolution.NothingToDo;

        if ((!headLive && waiting.Count == 0) || (group.BackupDueAt is { } end && now >= end))
        {
            // 5. Nobody took the job: what still waits lapses, as a single request does.
            return await LapseGroupAsync(state, now, notices, cancellationToken);
        }

        return ServiceRequestGroupResolution.NothingToDo;
    }

    /// <summary>
    /// Closes the backups that still wait for <paramref name="reason"/> and ends the group: the primary took the job, or the host cancelled.
    /// One save; the backups are told after the commit.
    /// </summary>
    private async Task<ServiceRequestGroupResolution> CloseWaitingBackupsAsync(
        GroupState state,
        string reason,
        ServiceRequestBackupClosedKind kind,
        ServiceRequestGroupResolution outcome,
        DateTime now,
        List<Func<Task>> notices,
        CancellationToken cancellationToken)
    {
        state.Group.Phase = ServiceRequestGroupPhase.Closed;
        state.Group.ClosedAt = now;

        var closed = state.Backups.Where(b => b.Status == ServiceRequestStatus.Richiesto).ToList();
        foreach (var backup in closed)
            CloseBySystem(backup, reason, now);

        await SaveAsync(state.Head!, "group closed", cancellationToken);
        foreach (var backup in closed)
        {
            // A backup that had accepted and is released because the primary took the job is told its time is free again.
            var closedBackup = backup;
            var kindForBackup = kind == ServiceRequestBackupClosedKind.PrimaryTookIt ? PrimaryTookItKind(backup) : kind;
            notices.Add(() => notifier.NotifyBackupClosedAsync(closedBackup, kindForBackup, cancellationToken));
        }

        logger.LogInformation("Group {GroupId} closed: {Outcome}, {Closed} backup requests closed", state.Group.Id, outcome, closed.Count);
        return outcome;
    }

    /// <summary>
    /// The second window: every supplier of the group that still waits can take the job until <see cref="ServiceRequestOptions.BackupResponseMinutes"/>
    /// from now, and the first to do so has it. The backups that did not accept are told. With nobody left to take it the group ends at once.
    /// </summary>
    private async Task<ServiceRequestGroupResolution> OpenBackupWindowAsync(
        GroupState state,
        DateTime now,
        List<Func<Task>> notices,
        CancellationToken cancellationToken)
    {
        var group = state.Group;
        var head = state.Head!;
        var waiting = state.Backups.Where(b => b.Status == ServiceRequestStatus.Richiesto).ToList();
        var headLive = head.Status == ServiceRequestStatus.Richiesto;
        if (!headLive && waiting.Count == 0)
            return await LapseGroupAsync(state, now, notices, cancellationToken);

        // The primary's window is over and it never answered (a refusal or a cancellation would have closed its request): it loses the points
        // of a missed deadline now, whatever happens next, unless it could not have answered (SP-REL). It stays in the race without priority.
        if (headLive)
        {
            var silent = head;
            var windowStart = PrimaryWindowStart(group, silent);
            var windowEnd = group.PrimaryDueAt;
            notices.Add(() => RecordMissedWindowAsync(silent, windowStart, windowEnd, cancellationToken));
        }

        group.Phase = ServiceRequestGroupPhase.BackupWindow;
        group.BackupDueAt = now.AddMinutes(options.Value.BackupResponseMinutes);
        if (headLive)
        {
            head.ResponseDueAt = group.BackupDueAt;
            head.UpdatedAt = now;
        }

        foreach (var backup in waiting)
        {
            backup.ResponseDueAt = group.BackupDueAt;
            backup.UpdatedAt = now;
        }

        await SaveAsync(head, "second window", cancellationToken);

        foreach (var backup in waiting)
        {
            var activated = backup;
            notices.Add(() => notifier.NotifyBackupActiveAsync(activated, group.BackupDueAt!.Value, cancellationToken));
        }

        logger.LogInformation(
            "Group {GroupId}: the primary is out, the backups can take the job until {BackupDueAt:O}", group.Id, group.BackupDueAt);
        return ServiceRequestGroupResolution.BackupWindowOpened;
    }

    /// <summary>
    /// Nobody took the job: what still waits (the primary, if it was never answered or cancelled, and the backups) lapses with
    /// <c>NoResponse</c> and the group ends. The host is told that nobody answered, whether it is the primary that lapses now or one that
    /// refused earlier; the suppliers whose requests lapse are told like for a single request.
    /// </summary>
    private async Task<ServiceRequestGroupResolution> LapseGroupAsync(
        GroupState state,
        DateTime now,
        List<Func<Task>> notices,
        CancellationToken cancellationToken)
    {
        var head = state.Head!;
        var lapsing = new List<ServiceRequest>();
        if (head.Status == ServiceRequestStatus.Richiesto)
        {
            CloseBySystem(head, ServiceRequestCancellationReasons.NoResponse, now);
            lapsing.Add(head);
        }

        foreach (var backup in state.Backups.Where(b => b.Status == ServiceRequestStatus.Richiesto))
        {
            CloseBySystem(backup, ServiceRequestCancellationReasons.NoResponse, now);
            lapsing.Add(backup);
        }

        state.Group.Phase = ServiceRequestGroupPhase.Closed;
        state.Group.ClosedAt = now;
        await SaveAsync(head, "group lapsed", cancellationToken);

        notices.Add(() => notifier.NotifyHostNoResponseAsync(head, cancellationToken));
        var group = state.Group;
        foreach (var request in lapsing)
        {
            // Nobody answered: each supplier that was asked and stayed silent loses the points of a missed deadline (the supplier the host
            // chose all of them, a backup its small share) unless it could not have answered, and its e-mail says so when it did (SP-REL).
            var lapsed = request;
            var isBackup = lapsed.GroupRole == ServiceRequestGroupRole.Backup;
            var windowStart = isBackup ? lapsed.CreatedAt : PrimaryWindowStart(group, lapsed);
            var windowEnd = isBackup ? group.BackupDueAt ?? group.PrimaryDueAt : group.PrimaryDueAt;
            notices.Add(async () =>
            {
                var lostPoints = await RecordMissedWindowAsync(lapsed, windowStart, windowEnd, cancellationToken);
                await notifier.NotifySupplierCancelledAsync(lapsed, cancellationToken, lostPoints);
            });
        }

        logger.LogInformation("Group {GroupId} lapsed: nobody took the job, {Lapsed} requests closed", state.Group.Id, lapsing.Count);
        return ServiceRequestGroupResolution.ClosedLapsed;
    }

    /// <summary>
    /// True for the refusals that mean "this backup cannot take the job": its time is not free, it is suspended or has to accept new Terms.
    /// Anything else is not the backup's doing and is not turned into a drop.
    /// </summary>
    private static bool IsBackupRefusal(Exception ex) => ex is
        DomainConflictException { Code: ServiceRequestErrorCodes.SlotUnavailable or ServiceRequestErrorCodes.CommitmentConflict }
        or DomainRuleException
        {
            Code: ServiceRequestErrorCodes.SupplierNotActive or ServiceRequestErrorCodes.TimeInvalid
                or SupplierActivation.TosReacceptanceRequiredCode,
        };

    /// <summary>
    /// A member of a group changed (refused, cancelled by its supplier or by the host, a proposed time turned down): the group is looked at right
    /// away, so the host does not wait for the next run of the job. Best effort: the job does the same, so a failure here is only logged.
    /// </summary>
    private async Task ResolveGroupAfterChangeAsync(Guid? groupId, CancellationToken cancellationToken)
    {
        if (groupId is not { } id)
            return;

        try
        {
            await ResolveGroupAsync(id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Group {GroupId} could not be followed right after a change of its requests (the job does it)", id);
        }
    }

    /// <summary>
    /// The host turned a proposed time of the primary down: the window the group is in starts again if it had passed while the host was
    /// deciding, as for a single request, and the requests of the group carry the same deadline.
    /// </summary>
    private async Task ReopenGroupWindowAsync(Guid groupId, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, GroupLock(groupId));
        await using (transaction)
        {
            var state = await LoadGroupAsync(groupId, cancellationToken);
            if (state is null || state.Head is not { } head)
                return;

            var now = Now();
            var group = state.Group;
            DateTime? due = null;
            if (group.Phase == ServiceRequestGroupPhase.PrimaryWindow && group.PrimaryDueAt <= now)
            {
                group.PrimaryDueAt = now.AddMinutes(options.Value.PrimaryResponseMinutes);
                due = group.PrimaryDueAt;
            }
            else if (group.Phase == ServiceRequestGroupPhase.BackupWindow && group.BackupDueAt is { } end && end <= now)
            {
                group.BackupDueAt = now.AddMinutes(options.Value.BackupResponseMinutes);
                due = group.BackupDueAt;
            }

            if (due is not { } newDue)
                return;

            foreach (var member in state.Members.Where(m => m.Status == ServiceRequestStatus.Richiesto))
            {
                member.ResponseDueAt = newDue;
                member.UpdatedAt = now;
            }

            await SaveAsync(head, "group window restarted", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
    }

    // ─── What the host sees ──────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<Guid, ServiceRequestGroupView>> GetGroupViewsAsync(
        Guid hostOrgId,
        IReadOnlyCollection<Guid> groupIds,
        CancellationToken cancellationToken = default)
    {
        if (groupIds.Count == 0)
            return new Dictionary<Guid, ServiceRequestGroupView>();

        // The groups of the host org only (TN-2 allow-list: the org is an explicit predicate), and their requests the same way.
        var groups = await db.ServiceRequestGroups
            .AsNoTracking()
            .Where(g => g.OrgId == hostOrgId && groupIds.Contains(g.Id))
            .ToListAsync(cancellationToken);
        if (groups.Count == 0)
            return new Dictionary<Guid, ServiceRequestGroupView>();

        var ids = groups.Select(g => g.Id).ToList();
        var members = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.OrgId == hostOrgId && r.GroupId != null && ids.Contains(r.GroupId.Value))
            .Select(r => new
            {
                r.Id,
                GroupId = r.GroupId!.Value,
                r.GroupRole,
                r.SupplierOrgId,
                SupplierName = r.SupplierOrg.DisplayName != "" ? r.SupplierOrg.DisplayName : r.SupplierOrg.Name,
                r.Status,
                r.BackupAcceptedAt,
                r.TakenAt,
                r.CancellationReason,
                r.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return groups.ToDictionary(
            group => group.Id,
            group => new ServiceRequestGroupView(
                group.Id,
                group.Phase,
                group.PrimaryDueAt,
                group.BackupDueAt,
                group.CreatedAt,
                group.ClosedAt,
                [.. members
                    .Where(m => m.GroupId == group.Id)
                    .OrderBy(m => RoleOrder(m.GroupRole))
                    .ThenBy(m => m.BackupAcceptedAt ?? DateTime.MaxValue)
                    .ThenBy(m => m.CreatedAt)
                    .ThenBy(m => m.Id)
                    .Select(m => new ServiceRequestGroupMemberView(
                        m.Id,
                        m.GroupRole,
                        m.SupplierOrgId,
                        m.SupplierName,
                        m.Status,
                        m.BackupAcceptedAt,
                        TookTheJob: m.TakenAt != null,
                        m.CancellationReason))]));
    }

    /// <summary>The order the host reads a group in: who it follows, who replaced or was replaced, then the backups.</summary>
    private static int RoleOrder(ServiceRequestGroupRole role) => role switch
    {
        ServiceRequestGroupRole.PromotedBackup => 0,
        ServiceRequestGroupRole.Primary => 0,
        ServiceRequestGroupRole.ReplacedPrimary => 1,
        _ => 2,
    };
}
