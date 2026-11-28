using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed partial class OwnerStayService
{
    private const int ReconcileBatch = 200;

    public const string ReviewReason = "owner_stay_to_review";

    public const string SyncFailedReason = "owner_stay_sync_failed";

    public async Task<OwnerStayReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var today = Today();
        var stays = LiveStays().AsNoTracking();
        var todos = StayTodos().AsNoTracking();
        var reviewType = nameof(ActivityType.OwnerStayToReview);
        var ids = new List<Guid>();

        ids.AddRange(await stays
            .Where(s => s.Status == OwnerStayStatus.Requested && (s.AutoApprove || !todos.Any(a => a.EntityId == s.Id && a.ActivityType == reviewType)))
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.Id)
            .Take(ReconcileBatch)
            .ToListAsync(cancellationToken));

        ids.AddRange(await stays
            .Where(s => s.Status == OwnerStayStatus.Approved && s.EndDate > today)
            .OrderBy(s => s.SyncCheckedAt)
            .Select(s => s.Id)
            .Take(ReconcileBatch)
            .ToListAsync(cancellationToken));

        ids.AddRange(await stays
            .Where(s => s.Status == OwnerStayStatus.Cancelled && s.CalendarBlockId != null
                        && (s.BlockReleasedAt == null || s.ReleaseSyncState == OwnerStaySyncState.Pending || s.ReleaseSyncState == OwnerStaySyncState.Failed))
            .OrderBy(s => s.SyncCheckedAt)
            .Select(s => s.Id)
            .Take(ReconcileBatch)
            .ToListAsync(cancellationToken));

        // The stays whose block (or its release) the channel manager refused and that have no «To do» of that since they failed: the one that was to be raised
        // when the state turned «failed» was lost (its creation failed after the state was saved, which the next run would not have seen as a change). Only
        // those, so the ones that have theirs never fill the batch.
        var syncFailedType = nameof(ActivityType.OwnerStaySyncFailed);
        var failing = stays
            .Where(s => s.EndDate > today
                        && ((s.Status == OwnerStayStatus.Approved && s.SyncState == OwnerStaySyncState.Failed)
                            || (s.Status == OwnerStayStatus.Cancelled && s.ReleaseSyncState == OwnerStaySyncState.Failed)));
        ids.AddRange(await failing
            .Where(s => !todos.Any(a => a.EntityId == s.Id && a.ActivityType == syncFailedType && a.CreatedAt >= s.UpdatedAt))
            .OrderBy(s => s.SyncCheckedAt)
            .Select(s => s.Id)
            .Take(ReconcileBatch)
            .ToListAsync(cancellationToken));

        var waiting = stays.Where(s => s.Status == OwnerStayStatus.Requested && !s.AutoApprove).Select(s => s.Id);
        var failingIds = failing.Select(s => s.Id);
        var live = stays.Select(s => s.Id);
        ids.AddRange(await todos
            .Where(a => a.CompletedAt == null && a.EntityId != null && live.Contains(a.EntityId.Value)
                        && ((a.ActivityType == reviewType && !waiting.Contains(a.EntityId.Value))
                            || (a.ActivityType != reviewType && !failingIds.Contains(a.EntityId.Value))))
            .Select(a => a.EntityId!.Value)
            .Take(ReconcileBatch)
            .ToListAsync(cancellationToken));

        var total = new Counter();
        Exception? firstFailure = null;
        foreach (var id in ids.Distinct())
        {
            try
            {
                await ProcessCoreAsync(id, total, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Owner stay {StayId}: the background work failed", id);
                firstFailure ??= ex;
                db.ChangeTracker.Clear();
            }
        }

        if (firstFailure is not null)
            throw new InvalidOperationException("The background work of the owner stays failed for at least one stay.", firstFailure);

        return total.Result();
    }

    public Task ProcessAsync(Guid stayId, CancellationToken cancellationToken = default) => ProcessCoreAsync(stayId, new Counter(), cancellationToken);

    private async Task ProcessCoreAsync(Guid stayId, Counter counter, CancellationToken cancellationToken)
    {
        var propertyId = await LiveStays().AsNoTracking().Where(s => s.Id == stayId).Select(s => (Guid?)s.PropertyId).FirstOrDefaultAsync(cancellationToken);
        if (propertyId is null)
            return;

        OwnerStayRequest stay;
        await using (var transaction = await LockAsync(propertyId.Value, cancellationToken))
        {
            stay = await Stays().FirstAsync(s => s.Id == stayId, cancellationToken);

            if (stay.Status == OwnerStayStatus.Requested && stay.AutoApprove)
                await AutoApproveAsync(stay, counter, cancellationToken);

            if (stay.Status == OwnerStayStatus.Cancelled && stay.CalendarBlockId is not null && stay.BlockReleasedAt is null)
            {
                await ReleaseCoreAsync(stay, null, cancellationToken);
                counter.Released++;
            }

            await RecordSyncAsync(stay, counter, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        await SyncTodosAsync(stay, counter, cancellationToken);
    }

    private async Task AutoApproveAsync(OwnerStayRequest stay, Counter counter, CancellationToken cancellationToken)
    {
        try
        {
            if (await ApproveCoreAsync(stay, null, automatic: true, cancellationToken))
                counter.Approved++;
        }
        catch (Exception ex) when (ex is DomainConflictException or DomainRuleException or NotFoundException)
        {
            logger.LogInformation("Owner stay {StayId}: the automatic approval was refused ({Code}); a person of the agency decides", stay.Id, CodeOf(ex));
            stay.AutoApprove = false;
            stay.UpdatedAt = Now();
        }
    }

    private async Task RecordSyncAsync(OwnerStayRequest stay, Counter counter, CancellationToken cancellationToken)
    {
        if (stay.CalendarBlockId is null)
            return;

        var processes = await ProcessesAsync([stay.BlockChangeProcessId, stay.ReleaseChangeProcessId], cancellationToken);
        var now = Now();
        if (stay.Status == OwnerStayStatus.Approved)
        {
            var live = BlockSync(stay, processes);
            if (live != stay.SyncState)
            {
                stay.SyncState = live;
                stay.SyncErrorCode = live == OwnerStaySyncState.Failed ? ErrorOf(stay.BlockChangeProcessId, processes) : null;

                if (live == OwnerStaySyncState.Failed)
                    stay.UpdatedAt = now;
                Audit(stay, null, live switch
                {
                    OwnerStaySyncState.Acknowledged => OwnerPortalAuditStatuses.SyncAcknowledged,
                    OwnerStaySyncState.Failed => OwnerPortalAuditStatuses.SyncFailed,
                    _ => OwnerPortalAuditStatuses.SyncRetried,
                });
                counter.SyncChanges++;
            }
        }
        else if (stay.Status == OwnerStayStatus.Cancelled && stay.BlockReleasedAt is not null)
        {
            var live = ReleaseSync(stay, processes);
            if (live != stay.ReleaseSyncState)
            {
                stay.ReleaseSyncState = live;
                stay.SyncErrorCode = live == OwnerStaySyncState.Failed ? ErrorOf(stay.ReleaseChangeProcessId, processes) : null;
                if (live == OwnerStaySyncState.Failed)
                    stay.UpdatedAt = now;
                Audit(stay, null, live switch
                {
                    OwnerStaySyncState.Acknowledged => OwnerPortalAuditStatuses.ReleaseAcknowledged,
                    OwnerStaySyncState.Failed => OwnerPortalAuditStatuses.ReleaseFailed,
                    _ => OwnerPortalAuditStatuses.SyncRetried,
                });
                counter.SyncChanges++;
            }
        }

        stay.SyncCheckedAt = now;
    }

    /// <summary>
    /// Keeps the «To do» of the agency in step with the stay: a request a person decides is on the list once (a person who closes it by hand does not get it
    /// back); a block the channel manager refused is on the list from the moment it was refused until it is acknowledged or the stay is over, once for each
    /// failure (a «To do» created since the stay last failed, which <c>UpdatedAt</c> records: one that is missing because its creation failed is created by
    /// the next run, and one a person closed by hand is not created again). The candidates of ReconcileAsync follow the same rules.
    /// </summary>
    private async Task SyncTodosAsync(OwnerStayRequest stay, Counter counter, CancellationToken cancellationToken)
    {
        var toReview = stay.Status == OwnerStayStatus.Requested && !stay.AutoApprove;
        var failed = stay.EndDate > Today()
                     && ((stay.Status == OwnerStayStatus.Approved && stay.SyncState == OwnerStaySyncState.Failed)
                         || (stay.Status == OwnerStayStatus.Cancelled && stay.ReleaseSyncState == OwnerStaySyncState.Failed));

        var open = await StayTodos()
            .AsNoTracking()
            .Where(a => a.OrgId == stay.OrgId && a.EntityId == stay.Id && a.CompletedAt == null)
            .Select(a => new { a.Id, a.ActivityType })
            .ToListAsync(cancellationToken);

        if (toReview && !await WasRaisedAsync(stay, ActivityType.OwnerStayToReview, cancellationToken))
        {
            await RaiseAsync(stay, ActivityType.OwnerStayToReview, ActivityUrgency.Medium, ReviewReason, cancellationToken);
            counter.TodosRaised++;
        }

        if (failed
            && !open.Any(item => item.ActivityType == nameof(ActivityType.OwnerStaySyncFailed))
            && !await WasRaisedSinceAsync(stay, ActivityType.OwnerStaySyncFailed, stay.UpdatedAt, cancellationToken))
        {
            await RaiseAsync(stay, ActivityType.OwnerStaySyncFailed, ActivityUrgency.High, SyncFailedReason, cancellationToken);
            counter.TodosRaised++;
        }

        foreach (var item in open)
        {
            var stillHolds = item.ActivityType == nameof(ActivityType.OwnerStayToReview) ? toReview : failed;
            if (stillHolds)
                continue;

            await activities.CloseAsync(item.Id, InternalActivity.ClosedBySystem, cancellationToken);
            counter.TodosClosed++;
        }
    }

    private IQueryable<OwnerStayRequest> LiveStays()
    {
        var properties = db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
        return Stays().Where(s => properties.Any(p => p.Id == s.PropertyId));
    }

    private IQueryable<InternalActivity> StayTodos() =>
        db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(a => a.Action == ActivityAction.ReviewOwnerStay && a.EntityType == StayEntityType);

    private Task<Guid> RaiseAsync(OwnerStayRequest stay, ActivityType type, ActivityUrgency urgency, string reason, CancellationToken cancellationToken) =>
        activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = type,
                Action = ActivityAction.ReviewOwnerStay,
                EntityType = StayEntityType,
                EntityId = stay.Id,
                Urgency = urgency,
                PropertyId = stay.PropertyId,
                OrgId = stay.OrgId,
                Deadline = stay.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Reason = reason,
            },
            cancellationToken);

    private Task<bool> WasRaisedAsync(OwnerStayRequest stay, ActivityType type, CancellationToken cancellationToken)
    {
        var typeName = type.ToString();
        return db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == stay.OrgId && a.Action != null && a.ActivityType == typeName && a.EntityType == StayEntityType && a.EntityId == stay.Id,
                cancellationToken);
    }

    /// <summary>An item of <paramref name="type"/> about the stay was created at <paramref name="since"/> or later, open or closed.</summary>
    private Task<bool> WasRaisedSinceAsync(OwnerStayRequest stay, ActivityType type, DateTime since, CancellationToken cancellationToken)
    {
        var typeName = type.ToString();
        return db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == stay.OrgId && a.Action != null && a.ActivityType == typeName && a.EntityType == StayEntityType && a.EntityId == stay.Id && a.CreatedAt >= since,
                cancellationToken);
    }

    private static string CodeOf(Exception ex) => ex switch
    {
        DomainException domain => domain.Code,
        NotFoundException notFound => notFound.Code ?? "not_found",
        _ => ex.GetType().Name,
    };

    private sealed class Counter
    {
        public int Approved { get; set; }

        public int Released { get; set; }

        public int SyncChanges { get; set; }

        public int TodosRaised { get; set; }

        public int TodosClosed { get; set; }

        public OwnerStayReconcileResult Result() => new(Approved, Released, SyncChanges, TodosRaised, TodosClosed);
    }
}
