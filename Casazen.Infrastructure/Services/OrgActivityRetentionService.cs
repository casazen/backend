using System.Globalization;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgActivityRetentionService" />
/// <remarks>
/// <para>A line goes when it is older than <c>OrgTeam:ActivityRetentionMonths</c> months (12 by default; a value that is missing,
/// not a number or below 1 is ignored, so a typo never deletes the log nor keeps it for ever). The months are calendar months
/// back from now, in UTC.</para>
/// <para>The lines are deleted in batches, oldest first, each in a transaction of its own, up to a few batches per run: a backlog
/// (the first run after a long stop) is taken in several nights rather than in one long statement. Idempotent by construction
/// (it deletes what matches, and a second run finds nothing) and safe with the services that write: they only insert, never
/// touch a line that is already there. A session advisory lock keeps two runs from overlapping.</para>
/// <para>The job has no tenant: every query ignores the tenant filter explicitly and acts on every org.</para>
/// </remarks>
public sealed class OrgActivityRetentionService(
    AppDbContext db,
    IConfiguration configuration,
    ILogger<OrgActivityRetentionService> logger,
    TimeProvider? timeProvider = null) : IOrgActivityRetentionService
{
    /// <summary>The key of the session lock of a run (<see cref="PostgresAdvisoryLocks.Scope.OrgActivityRetention"/>).</summary>
    internal const string RunLockKey = "org-activity-retention";

    /// <summary>Lines deleted per batch.</summary>
    internal const int BatchSize = 500;

    /// <summary>Batches per run: 50 000 lines a night; the next run takes the rest.</summary>
    internal const int MaxBatchesPerRun = 100;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OrgActivityRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.OrgActivityRetention, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Org activity retention skipped: another run is in progress");
            return OrgActivityRetentionResult.SkippedRun;
        }

        var cutoff = _clock.GetUtcNow().UtcDateTime.AddMonths(-RetentionMonths());

        var deleted = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var old = await db.OrgActivityEntries.IgnoreQueryFilters()
                .Where(e => e.When < cutoff)
                .OrderBy(e => e.When)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (old.Count == 0)
                break;

            db.OrgActivityEntries.RemoveRange(old);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            deleted += old.Count;
            if (old.Count < BatchSize)
                break;
        }

        if (deleted > 0)
            logger.LogInformation("Org activity retention: {Deleted} lines older than {Cutoff:O} deleted", deleted, cutoff);

        return new OrgActivityRetentionResult(false, deleted, cutoff);
    }

    /// <summary>
    /// The months a line is kept: the configured value when it is a whole number from 1 to <see cref="OrgActivityRules.MaxRetentionMonths"/>
    /// (a longer one is cut to it), 12 otherwise.
    /// </summary>
    internal int RetentionMonths() =>
        int.TryParse(configuration[OrgActivityRules.RetentionMonthsConfigKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var months)
        && months >= 1
            ? Math.Min(months, OrgActivityRules.MaxRetentionMonths)
            : OrgActivityRules.DefaultRetentionMonths;
}
