using Casazen.Core.Entities;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Publishes the domain events of the outbox on the event bus (ADR-022-R4, R5, R18), every minute.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Order and batches</b>: the undispatched events that are not dead letters
/// (<c>WHERE NOT DeadLettered AND DispatchedAt IS NULL ORDER BY OccurredAt</c>), <see cref="BatchSize"/> at a time; a run takes
/// the next batch while the last one was full and <see cref="RunBudget"/> is not spent, so it ends before the next run.</item>
/// <item><b>At least once</b>: an event is built through <see cref="DomainEventTypeMap"/> (its typed event when it has one) and
/// <c>DispatchedAt</c> is set only after every handler returned; the row is saved right after its event, so a crash loses at most
/// the event in flight, which the next run delivers again. Handlers are idempotent (ADR-022-R7).</item>
/// <item><b>Failures</b>: a failed delivery adds one to <c>Attempts</c> and keeps the error in <c>LastError</c>; the event is tried
/// again at the next run (once per run), and after <see cref="MaxAttempts"/> attempts it is dead-lettered and logged. A failing
/// event never stops the others.</item>
/// <item>A system job over every org (<c>IgnoreQueryFilters([TenantQueryFilter])</c>); each handler then runs in its own scope
/// under the org of its event (ADR-022-R6). Never two runs at once (<see cref="DisableConcurrentExecutionAttribute"/>, a timeout
/// shorter than the minute between runs). The logs carry ids and types, never a payload.</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 50)]
public class OutboxDispatcherJob(
    AppDbContext db,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcherJob> logger)
{
    public const string RecurringJobId = "outbox-dispatcher";
    public const string Cron = "*/1 * * * *";

    /// <summary>Events read at a time (ADR-022-R18).</summary>
    public const int BatchSize = 100;

    /// <summary>Failed deliveries after which an event is dead-lettered (ADR-022-R5).</summary>
    public const int MaxAttempts = 5;

    /// <summary>A run starts no new batch after this time, so it ends before the next run is due.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(40);

    // The next run comes a minute later and delivers what is left: a failed run is not retried.
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var failedThisRun = new List<Guid>();
        var dispatched = 0;
        var deadLettered = 0;

        while (true)
        {
            var excluded = failedThisRun.ToArray();
            var batch = await db.OutboxEvents
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(e => !e.DeadLettered && e.DispatchedAt == null && !excluded.Contains(e.Id))
                .OrderBy(e => e.OccurredAt)
                .ThenBy(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await DispatchAsync(row, cancellationToken))
                {
                    dispatched++;
                }
                else
                {
                    failedThisRun.Add(row.Id);
                    if (row.DeadLettered)
                        deadLettered++;
                }

                // One commit per event: a crash after this point never delivers this event again.
                await db.SaveChangesAsync(cancellationToken);
            }

            db.ChangeTracker.Clear();
            if (batch.Count < BatchSize || timeProvider.GetElapsedTime(started) >= RunBudget)
                break;
        }

        if (dispatched > 0 || failedThisRun.Count > 0)
        {
            logger.LogInformation(
                "Outbox dispatch: {Dispatched} events dispatched, {Failed} failed ({DeadLettered} dead-lettered)",
                dispatched,
                failedThisRun.Count,
                deadLettered);
        }
    }

    /// <summary>Publishes one event; true when every handler returned. A failure is recorded on the row.</summary>
    private async Task<bool> DispatchAsync(OutboxEvent row, CancellationToken cancellationToken)
    {
        try
        {
            await eventBus.PublishAsync(DomainEventTypeMap.Create(row), cancellationToken);
            row.DispatchedAt = timeProvider.GetUtcNow().UtcDateTime;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            row.Attempts++;
            row.LastError = Describe(ex);
            if (row.Attempts >= MaxAttempts)
            {
                row.DeadLettered = true;
                logger.LogWarning(
                    ex,
                    "Outbox event {EventId} ({EventType}) of org {OrgId} dead-lettered after {Attempts} failed deliveries",
                    row.Id,
                    row.EventType,
                    row.OrgId,
                    row.Attempts);
            }
            else
            {
                logger.LogWarning(
                    "Outbox event {EventId} ({EventType}) of org {OrgId} failed delivery {Attempts} of {MaxAttempts}; it is tried again at the next run",
                    row.Id,
                    row.EventType,
                    row.OrgId,
                    row.Attempts,
                    MaxAttempts);
            }

            return false;
        }
    }

    private static string Describe(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        return text.Length <= OutboxEvent.LastErrorMaxLength ? text : text[..OutboxEvent.LastErrorMaxLength];
    }
}
