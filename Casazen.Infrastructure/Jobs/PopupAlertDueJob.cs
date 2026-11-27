using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Pushes the popup alerts that fell due to the open sessions of their user (ADR-025-R3, R6), every minute: the ones the quiet hours held
/// back and the ones the user snoozed. <c>GET /api/me/alerts/pending</c> is read on page load and SignalR only pushes what is published
/// when it happens, so without this job a user who keeps the app open all night would never see the alert of 08:00 until the next page load.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What is due</b>: <c>RemindAt &lt;= now</c>, not dismissed and not pushed yet (<c>DeliveredAt IS NULL</c>). The job publishes each
/// one through <see cref="IPopupAlertPublisher"/> and sets <c>DeliveredAt</c>, committed one alert at a time, so an alert is pushed once and
/// not again until the user snoozes it again (a snooze clears <c>DeliveredAt</c>). An alert raised outside the quiet hours has no
/// <c>RemindAt</c> and was pushed by the router: it is never read here.</item>
/// <item><b>Order and budget</b>: the oldest <c>RemindAt</c> first, <see cref="BatchSize"/> at a time; a run takes the next batch while the
/// last one was full and <see cref="RunBudget"/> is not spent, so it ends before the next run. When the quiet hours of many users end at
/// the same minute the backlog is pushed within the run, the rest at the next.</item>
/// <item><b>Failures</b>: a failed alert is logged with its type and ids (never a user id) and stays due, so the next run tries again; it
/// never stops the others. The publisher itself never throws.</item>
/// <item>A system job over every org (<c>IgnoreQueryFilters([TenantQueryFilter])</c>). Never two runs at once
/// (<see cref="DisableConcurrentExecutionAttribute"/>, a timeout shorter than the minute between runs). The alert carries no text and no
/// personal data (ADR-025-R5).</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 50)]
public class PopupAlertDueJob(
    AppDbContext db,
    IPopupAlertPublisher publisher,
    TimeProvider timeProvider,
    ILogger<PopupAlertDueJob> logger)
{
    public const string RecurringJobId = "popup-alert-due";
    public const string Cron = "*/1 * * * *";

    /// <summary>Alerts read at a time.</summary>
    public const int BatchSize = 100;

    /// <summary>A run starts no new batch after this time, so it ends before the next run is due.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(40);

    // The next run comes a minute later and takes what is left due: a failed run is not retried.
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var failedThisRun = new List<Guid>();
        var pushed = 0;

        while (true)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var excluded = failedThisRun.ToArray();
            var batch = await db.PopupAlerts
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(a => a.RemindAt != null
                            && a.RemindAt <= now
                            && a.DismissedAt == null
                            && a.DeliveredAt == null
                            && !excluded.Contains(a.Id))
                .OrderBy(a => a.RemindAt)
                .ThenBy(a => a.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var alert in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await publisher.PublishAsync(
                        alert.UserId, alert.Id, alert.AlertType, alert.EntityType, alert.EntityId, alert.Urgency, cancellationToken);
                    alert.DeliveredAt = timeProvider.GetUtcNow().UtcDateTime;

                    // One commit per alert: a crash after this point never pushes this alert again.
                    await db.SaveChangesAsync(cancellationToken);
                    pushed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Out of the change tracker, so a save that failed is not tried again with the next alert.
                    db.Entry(alert).State = EntityState.Detached;
                    failedThisRun.Add(alert.Id);
                    logger.LogWarning(
                        ex,
                        "Popup alert {AlertId} ({AlertType} on {EntityType} {EntityId}) of org {OrgId} could not be pushed; it stays due for the next run",
                        alert.Id,
                        alert.AlertType,
                        alert.EntityType,
                        alert.EntityId,
                        alert.OrgId);
                }
            }

            db.ChangeTracker.Clear();
            if (batch.Count < BatchSize || timeProvider.GetElapsedTime(started) >= RunBudget)
                break;
        }

        if (pushed > 0 || failedThisRun.Count > 0)
            logger.LogInformation("Popup alerts due: {Pushed} pushed, {Failed} failed", pushed, failedThisRun.Count);
    }
}
