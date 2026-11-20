using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The five-year retention of the documents of the Ticino police rules (NF-128 AC3, casazen/backend#979, decisions D10 and D11), every day at 03:55 UTC:
/// the receipts of the police notifications, the sheets of the groups of six or more and the written authorizations of the parents of unaccompanied
/// minors (<see cref="SwissComplianceReceipt"/> of the types <see cref="SwissComplianceReceipts.RetainedFiveYears"/>) whose document date
/// (<see cref="SwissComplianceReceipt.ReportDate"/>) is more than five years ago are deleted: the encrypted file in the private bucket, then the row.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what is expired.</b> The cut is today (Europe/Zurich, whose rules Europe/Rome shares) minus <see cref="SwissComplianceReceipts.PoliceRetentionYears"/>
/// years: a document dated before that day goes, one dated on it or later is never read for deletion, so a legal proof is never deleted early.</item>
/// <item><b>The OTR reports are kept.</b> They are tax evidence of the overnight stays, for which no retention rule is decided: never deleted here.</item>
/// <item><b>The file goes before the row</b>, so a failure of the storage leaves the row for the next night; a document whose file could not be
/// deleted is left as it is and the run fails at its end (Hangfire repeats it): nothing past its time is kept in silence. A file that is already
/// gone (a receipt of before the archive kept originals) is not a failure.</item>
/// <item><b>A system job over every org</b> (<c>IgnoreQueryFilters([TenantQueryFilter])</c>), in batches of <see cref="BatchSize"/>. The file of a document is
/// found by its own org, property and id (<see cref="StorageKeys.SwissComplianceReceipt"/>), never another org's. It logs counts and the cutoff day,
/// never a key, a property or a person.</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SwissComplianceRetentionJob(
    AppDbContext db,
    IFileStorage storage,
    TimeProvider timeProvider,
    ILogger<SwissComplianceRetentionJob> logger)
{
    public const string RecurringJobId = "swiss-compliance-retention";

    /// <summary>Every day at 03:55 UTC, after the retention of the compliance proofs (03:40) and the purge of the guest documents (03:50).</summary>
    public const string Cron = "55 3 * * *";

    /// <summary>Documents removed by one save.</summary>
    public const int BatchSize = 100;

    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var cutoffDay = timeProvider.TodayInRomeAsDateOnly().AddYears(-SwissComplianceReceipts.PoliceRetentionYears);

        // The report date is stored as midnight UTC of its day (the convention of the stays): a document dated before the cut day is past its time.
        var cutoff = DateTime.SpecifyKind(cutoffDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var types = SwissComplianceReceipts.RetainedFiveYears.ToList();

        var removed = 0;
        var failed = new HashSet<Guid>();
        Exception? firstFailure = null;
        while (true)
        {
            var skipped = failed.ToList();
            var due = await db.SwissComplianceReceipts
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(r => types.Contains(r.Type) && r.ReportDate < cutoff && !skipped.Contains(r.Id))
                .OrderBy(r => r.ReportDate)
                .ThenBy(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;

            var gone = new List<SwissComplianceReceipt>(due.Count);
            foreach (var receipt in due)
            {
                try
                {
                    await storage.DeleteAsync(
                        StorageBucket.Private, StorageKeys.SwissComplianceReceipt(receipt.OrgId, receipt.PropertyId, receipt.Id), cancellationToken);
                    gone.Add(receipt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(receipt.Id);
                    firstFailure ??= ex;
                }
            }

            db.SwissComplianceReceipts.RemoveRange(gone);
            await db.SaveChangesAsync(cancellationToken);
            removed += gone.Count;
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Swiss compliance retention (documents dated before {Cutoff:yyyy-MM-dd}): {Removed} removed, {Failed} could not be removed",
            cutoff,
            removed,
            failed.Count);

        // Nothing past its time is kept in silence: the run fails (and is repeated) while a file could not be deleted; what was removed stays removed.
        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failed.Count} Swiss compliance document(s) past their retention could not be removed from the storage.",
                firstFailure);
        }
    }
}
