using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Deletes the abandoned lease drafts (LR-12, decision LR-D33, <see cref="ILeaseDraftRetentionService"/>): a draft holds the names,
/// fiscal codes and residences of people and is not kept for ever. One that nobody changed for
/// <c>LongRentLease:DraftRetentionDays</c> days (180 by default) is deleted with its parties, events and files; one with a signed
/// copy, or a rent plan, never is.
/// </summary>
/// <remarks>
/// <para>Idempotent by construction: it deletes what matches, and a second run finds nothing. Each draft is deleted in a transaction
/// of its own under the row lock the guided draft takes, after the conditions are checked again, so a draft that was saved or
/// signed since the list was read is left alone. The drafts go in batches, oldest first, a few batches per run: the first run after a
/// long stop takes the backlog over several nights rather than in one long job. A session advisory lock keeps two runs apart.</para>
/// <para>The job has no tenant (the filter is ignored explicitly) and logs only counts: never an id of a party, never a name.</para>
/// </remarks>
public sealed class LeaseDraftRetentionService(
    AppDbContext db,
    LeaseDraftDeleter deleter,
    IOptions<LongRentLeaseOptions> options,
    ILogger<LeaseDraftRetentionService> logger,
    TimeProvider? timeProvider = null) : ILeaseDraftRetentionService
{
    /// <summary>The key of the session lock of a run (<see cref="PostgresAdvisoryLocks.Scope.LeaseDraftRetentionRun"/>).</summary>
    internal const string RunLockKey = "lease-draft-retention";

    /// <summary>Drafts read per batch.</summary>
    internal const int BatchSize = 100;

    /// <summary>Batches per run: 5,000 drafts a night; the next run takes the rest.</summary>
    internal const int MaxBatchesPerRun = 50;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<LeaseDraftRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.LeaseDraftRetentionRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Lease draft retention skipped: another run is in progress");
            return LeaseDraftRetentionResult.SkippedRun;
        }

        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-options.Value.DraftRetentionDays);

        var deleted = 0;
        // The drafts this run could not delete (busy, saved in the meantime, refused): not read again, so one that refuses to go at the
        // head of the list cannot stop the ones behind it, night after night.
        var left = new HashSet<Guid>();
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var skip = left.ToArray();
            var ids = await db.LeaseContracts
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(l => l.Status == LeaseStatus.Draft && l.SignedPdfStoragePath == null && l.UpdatedAt < cutoff && !skip.Contains(l.Id))
                .OrderBy(l => l.UpdatedAt)
                .ThenBy(l => l.Id)
                .Select(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
                break;

            var deletedInBatch = 0;
            foreach (var id in ids)
            {
                if (await TryDeleteAsync(id, cutoff, cancellationToken))
                    deletedInBatch++;
                else
                    left.Add(id);
            }

            deleted += deletedInBatch;
            // Fewer than a full batch: nothing is left to read. A batch that deleted nothing is not read again either (its drafts are in `left`).
            if (ids.Count < BatchSize)
                break;
        }

        if (deleted > 0 || left.Count > 0)
        {
            logger.LogInformation(
                "Lease draft retention: {Deleted} drafts not changed since {Cutoff:O} deleted, {Left} left for the next run",
                deleted, cutoff, left.Count);
        }

        return new LeaseDraftRetentionResult(false, deleted, cutoff);
    }

    /// <summary>Deletes one draft if it is still abandoned once the row is locked; false when it was left (changed, signed) or could not be deleted.</summary>
    internal async Task<bool> TryDeleteAsync(Guid leaseId, DateTime cutoff, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files;
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            db.ChangeTracker.Clear();
            await LeaseDraftLocking.LockRowAsync(db, leaseId, cancellationToken);

            var lease = await deleter.WithChildren()
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken);
            if (lease is null || lease.UpdatedAt >= cutoff || !await deleter.IsDeletableAsync(lease, cancellationToken))
                return false;

            files = await deleter.DeleteAsync(lease, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (DomainConflictException)
        {
            // Not deletable after all (a record that refers to it): left for the owner, who will see it in the list.
            return false;
        }
        catch (Exception ex) when (LeaseDraftLocking.IsContention(ex))
        {
            // Somebody is working on it right now: it is not abandoned. The next run looks again.
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One draft that cannot be read or deleted (a value that cannot be decrypted, a record added after this code was written)
            // must not stop the run before the ones behind it. Its id is not personal data; what failed is in the exception.
            logger.LogWarning(ex, "Lease draft retention: a draft could not be deleted and is left. LeaseId={LeaseId}", leaseId);
            return false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        await deleter.DeleteFilesAsync(files);
        return true;
    }
}
