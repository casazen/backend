using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The five-year retention of the proofs of the compliance duties (E20/F02/US01/T01, NF-47, NF-52, CHK-08, AC-NF52-12), every day at 03:40 UTC: the file and the
/// row of a <see cref="ManualComplianceProof"/> whose <see cref="ManualComplianceProof.RetainUntil"/> has passed are deleted, together with the lines of the flats
/// it covers (cascade). A proof is kept <see cref="ManualComplianceProof.RetentionYears"/> years from its upload and shown until this job removes it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what is expired.</b> A proof whose time has not passed is never read for deletion, whatever its kind and its org; a proof is never deleted
/// by anything else (the archive only reads and adds).</item>
/// <item><b>The file goes before the row</b>, so a failure of the storage leaves the row for the next night. A proof whose file could not be deleted is
/// left as it is and the run fails at its end (Hangfire repeats it): nothing is silently kept past its time. A file that is already gone is not a failure
/// (the storage deletes what is not there without complaining), so a night that was interrupted after the files can be repeated.</item>
/// <item><b>A system job over every org</b> (<c>IgnoreQueryFilters([TenantQueryFilter])</c>), in batches of <see cref="BatchSize"/>. It logs counts and the
/// cutoff day, never a key, a flat or a person.</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ComplianceProofRetentionJob(
    AppDbContext db,
    IFileStorage storage,
    TimeProvider timeProvider,
    ILogger<ComplianceProofRetentionJob> logger)
{
    public const string RecurringJobId = "compliance-proof-retention";

    /// <summary>Every day at 03:40 UTC, after the other retention jobs of the night.</summary>
    public const string Cron = "40 3 * * *";

    /// <summary>Proofs removed by one save.</summary>
    public const int BatchSize = 100;

    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // RetainUntil is the upload plus five years and is not stored, so a proof is due when its upload is older than five years from today. Taking
        // the five years off today can only be a day late for a proof uploaded on 29 February, never early.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddYears(-ManualComplianceProof.RetentionYears);

        var removed = 0;
        var failed = new HashSet<Guid>();
        Exception? firstFailure = null;
        while (true)
        {
            var skipped = failed.ToList();
            var due = await db.ManualComplianceProofs
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Include(p => p.CoveredProperties)
                .Where(p => p.UploadedAt < cutoff && !skipped.Contains(p.Id))
                .OrderBy(p => p.UploadedAt)
                .ThenBy(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;

            var gone = new List<ManualComplianceProof>(due.Count);
            foreach (var proof in due)
            {
                try
                {
                    if (StorageKeys.IsValid(proof.FileKey))
                        await storage.DeleteAsync(StorageBucket.Private, proof.FileKey, cancellationToken);

                    gone.Add(proof);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(proof.Id);
                    firstFailure ??= ex;
                }
            }

            db.ManualComplianceProofs.RemoveRange(gone);
            await db.SaveChangesAsync(cancellationToken);
            removed += gone.Count;
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Compliance proof retention (uploaded before {Cutoff:yyyy-MM-dd}): {Removed} proofs removed, {Failed} could not be removed",
            cutoff,
            removed,
            failed.Count);

        // Nothing past its time is kept in silence: the run fails (and is repeated) while a file could not be deleted; what was removed stays removed.
        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failed.Count} compliance proof(s) past their retention could not be removed from the storage.",
                firstFailure);
        }
    }
}
