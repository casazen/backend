using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestGroupRunService"/>
/// <remarks>
/// <b>A system job.</b> It works across every org on purpose: the groups it resolves are the ones whose time has passed, whoever they belong
/// to. It is run only by Hangfire (<c>service-request-groups</c>) and reachable from no request; the statement on
/// <c>ServiceRequestGroups</c> is <see cref="DueIdsOf"/>, which <c>ServiceRequestGroupTenancyTests</c> lists as the one system statement.
/// Everything else is <see cref="IServiceRequestService.ResolveGroupAsync"/>, one group at a time, each under its own lock.
/// </remarks>
public sealed class ServiceRequestGroupRunService(
    AppDbContext db,
    IServiceRequestService requests,
    ILogger<ServiceRequestGroupRunService> logger,
    TimeProvider? timeProvider = null) : IServiceRequestGroupRunService
{
    internal const string RunLockKey = "service-request-groups";

    /// <summary>
    /// Groups one run handles at most: a run that stays short is better than one that holds the lock for minutes, and the next run (5
    /// minutes later) goes on.
    /// </summary>
    public const int MaxPerRun = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceRequestGroupRun> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceRequestGroupRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Service request groups run skipped: another run is in progress");
            return ServiceRequestGroupRun.LockTaken;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var dueIds = await DueIdsOf(db, now).ToListAsync(cancellationToken);
        if (dueIds.Count == 0)
            return ServiceRequestGroupRun.Empty;

        int closed = 0, windows = 0, conflicts = 0, failed = 0;
        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await requests.ResolveGroupAsync(id, cancellationToken))
                {
                    case ServiceRequestGroupResolution.ClosedPrimaryTook
                        or ServiceRequestGroupResolution.ClosedBackupTook
                        or ServiceRequestGroupResolution.ClosedLapsed
                        or ServiceRequestGroupResolution.ClosedCancelled:
                        closed++;
                        break;
                    case ServiceRequestGroupResolution.BackupWindowOpened:
                        windows++;
                        break;
                    case ServiceRequestGroupResolution.Conflict:
                        conflicts++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Group {GroupId} could not be resolved by the run (retried by the next run)", id);
            }
        }

        logger.LogInformation(
            "Service request groups: {Examined} examined, {Closed} closed, {Windows} second windows opened, {Conflicts} changed under the run, {Failed} failed",
            dueIds.Count, closed, windows, conflicts, failed);
        return new ServiceRequestGroupRun(Skipped: false, dueIds.Count, closed, windows, conflicts, failed);
    }

    /// <summary>
    /// The groups that are not over and have something due at <paramref name="nowUtc"/>, the oldest window first, at most
    /// <see cref="MaxPerRun"/>: the first window passed, the second one passed, or the request the host follows moved on (taken, refused,
    /// cancelled by the host) without the group having followed. ServiceRequestGroup has more than two parties and no tenant filter (TN-2
    /// allow-list): the job works across every org, so the predicate is the state of the group itself.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> DueIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ServiceRequestGroups
            .AsNoTracking()
            .Where(g => g.Phase != ServiceRequestGroupPhase.Closed
                && ((g.Phase == ServiceRequestGroupPhase.PrimaryWindow && g.PrimaryDueAt <= nowUtc)
                    || (g.Phase == ServiceRequestGroupPhase.BackupWindow && g.BackupDueAt != null && g.BackupDueAt <= nowUtc)
                    // The request the host follows moved on and the group did not (a crash between the two, a call that failed).
                    || db.ServiceRequests.Any(r => r.GroupId == g.Id
                        && (r.GroupRole == ServiceRequestGroupRole.Primary || r.GroupRole == ServiceRequestGroupRole.PromotedBackup)
                        && (r.Status == ServiceRequestStatus.PresoInCarico
                            || r.Status == ServiceRequestStatus.InCorso
                            || r.Status == ServiceRequestStatus.Completato
                            || r.Status == ServiceRequestStatus.Pagato
                            || (r.Status == ServiceRequestStatus.Annullato && r.CancelledBy == ServiceRequestActorParty.Host)
                            || (g.Phase == ServiceRequestGroupPhase.PrimaryWindow && r.Status != ServiceRequestStatus.Richiesto)))))
            .OrderBy(g => g.Phase == ServiceRequestGroupPhase.BackupWindow ? g.BackupDueAt : g.PrimaryDueAt)
            .ThenBy(g => g.Id)
            .Select(g => g.Id)
            .Take(MaxPerRun);
}
