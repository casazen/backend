using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Keeps the outbox from growing for ever without losing the trail of what failed (ADR-022-R15, R21), every night at 03:10 UTC.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The <b>dispatched</b> events whose <c>DispatchedAt</c> is older than <c>EventBus:RetentionDays</c> (default 60, never below
/// 30: the test mode of the routines replays 30 days) are deleted, <see cref="BatchSize"/> rows per statement. An event not dispatched
/// yet is never touched.</item>
/// <item>A <b>dead letter</b> is never deleted by the job. Once older than the retention (by <c>OccurredAt</c>, it was never
/// dispatched) it is anonymized, once: <c>PayloadJson</c> becomes <c>{}</c>, <c>LastError</c> is emptied and <c>EntityId</c> becomes
/// <see cref="Guid.Empty"/>, as the anonymization of ADR-022 (amendment of 2026-11-03) says, so nothing links it to a booking or a
/// guest any more; the org, the type, the dates and the attempts stay for the audit.</item>
/// <item>A system job over every org (<c>IgnoreQueryFilters([TenantQueryFilter])</c>). It logs the counts, never a payload.</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class OutboxCleanupJob(
    AppDbContext db,
    IOptions<EventBusOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxCleanupJob> logger)
{
    public const string RecurringJobId = "outbox-cleanup";
    public const string Cron = "10 3 * * *"; // daily at 03:10 UTC

    /// <summary>Rows deleted by one statement.</summary>
    public const int BatchSize = 1000;

    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var retentionDays = options.Value.EffectiveRetentionDays;
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);

        var deleted = await DeleteDispatchedAsync(cutoff, cancellationToken);
        var anonymized = await AnonymizeDeadLettersAsync(cutoff, cancellationToken);

        logger.LogInformation(
            "Outbox cleanup (retention {RetentionDays} days, before {Cutoff:O}): {Deleted} dispatched events deleted, {Anonymized} dead letters anonymized",
            retentionDays,
            cutoff,
            deleted,
            anonymized);
    }

    private async Task<int> DeleteDispatchedAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;
        while (true)
        {
            var ids = await Outbox()
                .Where(e => !e.DeadLettered && e.DispatchedAt != null && e.DispatchedAt < cutoff)
                .OrderBy(e => e.DispatchedAt)
                .Select(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
                break;

            deleted += await Outbox()
                .Where(e => ids.Contains(e.Id) && !e.DeadLettered && e.DispatchedAt != null)
                .ExecuteDeleteAsync(cancellationToken);
            if (ids.Count < BatchSize)
                break;
        }

        return deleted;
    }

    private Task<int> AnonymizeDeadLettersAsync(DateTime cutoff, CancellationToken cancellationToken) =>
        Outbox()
            .Where(e => e.DeadLettered
                        && e.OccurredAt < cutoff
                        && (e.PayloadJson != OutboxEvent.EmptyPayload || e.LastError != null || e.EntityId != Guid.Empty))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.PayloadJson, OutboxEvent.EmptyPayload)
                    .SetProperty(e => e.LastError, (string?)null)
                    .SetProperty(e => e.EntityId, Guid.Empty),
                cancellationToken);

    private IQueryable<OutboxEvent> Outbox() => db.OutboxEvents.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
}
