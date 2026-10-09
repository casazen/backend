using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestExpiryService"/>
/// <remarks>
/// <para><b>A system job.</b> It works across every supplier on purpose: the holds it deletes are the ones whose time has passed, and
/// the requests it cancels are the ones whose deadline has passed, whoever they belong to. It is run only by Hangfire
/// (<c>service-request-expiry</c>) and reachable from no request; the statement on <c>ShowcaseBookingHolds</c> is
/// <see cref="ExpiredHoldIdsOf"/>, which <c>ShowcaseBookingTenancyTests</c> lists as the one system statement.</para>
/// <para><b>Nothing to lock but the run.</b> A hold past its expiry no longer counts for the slot planner and cannot be checked
/// anymore (the check refuses it under the supplier's lock), so deleting it needs no lock of the calendar. The request
/// cancellation is <see cref="IServiceRequestAutoCancelService.CancelUnansweredShowcaseAsync"/>: one request at a time, saved
/// only if nobody touched it since it was read.</para>
/// </remarks>
public sealed class ServiceRequestExpiryService(
    AppDbContext db,
    IServiceRequestAutoCancelService autoCancel,
    ILogger<ServiceRequestExpiryService> logger,
    TimeProvider? timeProvider = null) : IServiceRequestExpiryService
{
    internal const string RunLockKey = "service-request-expiry";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceRequestExpiryRun> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceRequestExpiryRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Service request expiry run skipped: another run is in progress");
            return ServiceRequestExpiryRun.LockTaken;
        }

        var holdsDeleted = await DeleteExpiredHoldsAsync(cancellationToken);
        var cancellation = await autoCancel.CancelUnansweredShowcaseAsync(cancellationToken);

        if (holdsDeleted > 0 || cancellation.Cancelled > 0 || cancellation.Failed > 0)
        {
            logger.LogInformation(
                "Service request expiry: {Holds} holds deleted, {Cancelled} showcase requests cancelled, {Conflicts} changed under the run, {Failed} failed",
                holdsDeleted, cancellation.Cancelled, cancellation.Conflicts, cancellation.Failed);
        }

        return new ServiceRequestExpiryRun(Skipped: false, holdsDeleted, cancellation.Cancelled, cancellation.Conflicts, cancellation.Failed);
    }

    /// <summary>
    /// The holds whose expiry has passed at <paramref name="nowUtc"/>, the oldest first, at most
    /// <see cref="ShowcaseBookingLimits.ExpiryBatchSize"/>: the ones nobody checked and the ones that were checked and kept for the
    /// replay of the link. Only ids: the payload is not read.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Guid> ExpiredHoldIdsOf(AppDbContext db, DateTime nowUtc) =>
        db.ShowcaseBookingHolds
            .AsNoTracking()
            .Where(h => h.ExpiresAt <= nowUtc)
            .OrderBy(h => h.ExpiresAt)
            .ThenBy(h => h.Id)
            .Select(h => h.Id)
            .Take(ShowcaseBookingLimits.ExpiryBatchSize);

    private async Task<int> DeleteExpiredHoldsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var ids = await ExpiredHoldIdsOf(db, now).ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        // By key, from a stub: the rows are deleted without being read (their payload is personal data and is encrypted).
        db.ChangeTracker.Clear();
        db.ShowcaseBookingHolds.RemoveRange(ids.Select(id => new ShowcaseBookingHold { Id = id }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another run (or a check of the link) removed or changed some of them first: the next run finds what is left.
            db.ChangeTracker.Clear();
            logger.LogInformation("Service request expiry: some expired holds were already gone");
            return 0;
        }

        return ids.Count;
    }
}
