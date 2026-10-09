using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestAutoCancelService"/>
/// <remarks>
/// <para>Two runs share the code that cancels one request: the one of the hosts' requests (SP-04,
/// <see cref="CancelUnansweredAsync"/>, behind the flag <c>SupplierRequestAutoCancel</c>, a session lock of its own) and the one
/// of the requests from the suppliers' public showcases (SP-10, <see cref="CancelUnansweredShowcaseAsync"/>, run by
/// <see cref="ServiceRequestExpiryService"/> under its lock). They read disjoint sets (the context of the request decides), so
/// neither cancels what the other owns, and the cancellation is the same: <c>Richiesto → Annullato</c> by CasaZen with the reason
/// <c>NoResponse</c>, the other parties told by <see cref="ServiceRequestNotifier"/> (the host for a host's request, the customer
/// for a showcase one).</para>
/// </remarks>
public sealed class ServiceRequestAutoCancelService(
    AppDbContext db,
    ServiceRequestNotifier notifier,
    IFeatureFlags featureFlags,
    ILogger<ServiceRequestAutoCancelService> logger,
    TimeProvider? timeProvider = null) : IServiceRequestAutoCancelService
{
    internal const string RunLockKey = "service-request-auto-cancel";

    /// <summary>
    /// Requests one run handles at most: the first run after the flag is turned on may find many overdue requests, and a run
    /// that stays short is better than one that holds the lock for minutes. The next run (10 minutes later) goes on.
    /// </summary>
    public const int MaxPerRun = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceRequestAutoCancelRun> CancelUnansweredAsync(CancellationToken cancellationToken = default)
    {
        // Also checked here and not only when the job is scheduled: a run triggered by hand, or by a schedule an earlier deploy
        // left behind, does nothing while the flag is off.
        if (!featureFlags.IsEnabled(FeatureFlags.SupplierRequestAutoCancel))
            return ServiceRequestAutoCancelRun.FlagOff;

        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceRequestAutoCancelRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Service request auto-cancel run skipped: another run is in progress");
            return ServiceRequestAutoCancelRun.LockTaken;
        }

        return await CancelDueAsync(showcase: false, cancellationToken);
    }

    public Task<ServiceRequestAutoCancelRun> CancelUnansweredShowcaseAsync(CancellationToken cancellationToken = default) =>
        CancelDueAsync(showcase: true, cancellationToken);

    private async Task<ServiceRequestAutoCancelRun> CancelDueAsync(bool showcase, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var dueIds = await (showcase ? DueShowcaseIdsOf(db, now) : DueIdsOf(db, now)).ToListAsync(cancellationToken);
        if (dueIds.Count == 0)
            return ServiceRequestAutoCancelRun.Empty;

        int cancelled = 0, conflicts = 0, failed = 0;
        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await CancelAsync(id, now, showcase, cancellationToken))
            {
                case Outcome.Cancelled:
                    cancelled++;
                    break;
                case Outcome.Conflict:
                    conflicts++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        logger.LogInformation(
            "Service request auto-cancel ({Scope}): {Cancelled} cancelled, {Conflicts} changed under the run, {Failed} failed",
            showcase ? "showcase" : "hosts", cancelled, conflicts, failed);
        return new ServiceRequestAutoCancelRun(Disabled: false, Skipped: false, cancelled, conflicts, failed);
    }

    /// <summary>
    /// The new requests of the hosts whose deadline has passed at <paramref name="nowUtc"/> and that have no time proposed, the
    /// oldest deadline first, at most <see cref="MaxPerRun"/>. ServiceRequest has two parties and no tenant filter (TN-2
    /// allow-list): the job works across every org, so the filters of the host are off and the predicate is the state of the
    /// request itself. A request of the public showcase is not one of them (it is
    /// <see cref="DueShowcaseIdsOf"/>).
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> DueIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext != ServiceRequestRentalContext.Showcase
                        && r.Status == ServiceRequestStatus.Richiesto
                        && r.ResponseDueAt != null
                        && r.ResponseDueAt <= nowUtc
                        && r.ProposedStartUtc == null)
            .OrderBy(r => r.ResponseDueAt)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .Take(MaxPerRun);

    /// <summary>
    /// The new requests of the public showcases whose deadline has passed at <paramref name="nowUtc"/>, the oldest deadline
    /// first, at most <see cref="MaxPerRun"/>. A pending proposal of another time does not exempt them: the proposal moved the
    /// deadline to the instant the customer has to answer by, so the one that is due is the one nobody answered.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> DueShowcaseIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.Status == ServiceRequestStatus.Richiesto
                        && r.ResponseDueAt != null
                        && r.ResponseDueAt <= nowUtc)
            .OrderBy(r => r.ResponseDueAt)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .Take(MaxPerRun);

    private enum Outcome
    {
        Cancelled,
        Conflict,
        Failed,
    }

    private async Task<Outcome> CancelAsync(Guid id, DateTime now, bool showcase, CancellationToken cancellationToken)
    {
        // One request at a time, read again: what the run listed a moment ago may have been answered since.
        db.ChangeTracker.Clear();
        try
        {
            // IgnoreQueryFilters: see DueIdsOf. The property (none for a showcase request) and the supplier org are read for the
            // notifications.
            var request = await db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (request is null
                || (request.RentalContext == ServiceRequestRentalContext.Showcase) != showcase
                || request.ResponseDueAt is not { } due
                || due > now
                || (!showcase && request.ProposedStartUtc is not null)
                || !ServiceRequestStateMachine.CanCancel(request.Status, ServiceRequestActorParty.System))
            {
                return Outcome.Conflict;
            }

            request.Status = ServiceRequestStatus.Annullato;
            request.CancelledAt = now;
            request.CancelledBy = ServiceRequestActorParty.System;
            request.CancellationReason = ServiceRequestCancellationReasons.NoResponse;
            request.ResponseDueAt = null;

            // A showcase request can lapse with a time proposed that its customer never answered: a cancelled request has no
            // proposal waiting (a host's request with one is never picked by this job, so for it this changes nothing).
            ServiceRequestService.ClearProposal(request);
            request.UpdatedAt = now;

            // Saved only if the request is as it was read (xmin): a supplier that took it meanwhile wins.
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("ServiceRequest {Id}: Richiesto -> Annullato, no answer before the deadline", id);

            // After the save, by the winner only: the host (or the customer of a showcase request) is told that nobody answered,
            // the supplier that it lost the request.
            await notifier.NotifyHostAsync(request, cancellationToken);
            await notifier.NotifySupplierCancelledAsync(request, cancellationToken);
            return Outcome.Cancelled;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("ServiceRequest {Id}: changed while the auto-cancel run was cancelling it, left as it is", id);
            return Outcome.Conflict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ServiceRequest {Id} could not be cancelled by the auto-cancel run (retried by the next run)", id);
            return Outcome.Failed;
        }
    }
}
