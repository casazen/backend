using System.Runtime.ExceptionServices;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The background work of the owner portal operations (E07/F07/US01, NF-111): <see cref="ProcessStayAsync"/> finishes one owner stay right after an owner
/// asked for it or withdrew it (the automatic approval and its block, the block to give back), and <see cref="ExecuteAsync"/> is the safety net that runs
/// every five minutes: the same for every stay that waits, the sync results of the blocks and the «To do» of the agency.
/// </summary>
/// <remarks>
/// <para>Behind <c>Features:AgencyTier</c>, the flag of the agency owner portal: the schedule is removed while it is off, and a run enqueued before does
/// nothing. The channel manager itself is reached only through the calendar changes, which follow <c>Features:OctorateConnector</c>.</para>
/// <para>It lives next to the other jobs because the services of this project enqueue it after their save is committed and cannot see the web project.
/// No automatic retry of the run enqueued after an owner request: what could not be done is done by the recurring run.</para>
/// </remarks>
public class OwnerPortalOperationsJob(
    IOwnerStayService stays,
    IOwnerCollaborationService collaboration,
    IFeatureFlags featureFlags,
    ILogger<OwnerPortalOperationsJob> logger)
{
    public const string RecurringJobId = "owner-portal-operations";

    /// <summary>Every five minutes.</summary>
    public const string Cron = "*/5 * * * *";

    /// <summary>The seconds a run waits for the previous run of the same job.</summary>
    public const int LockTimeoutSeconds = 60;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("OwnerPortalOperationsJob.ProcessStayAsync:{0}", LockTimeoutSeconds)]
    public async Task ProcessStayAsync(Guid stayId, CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.AgencyTier))
        {
            logger.LogInformation("Owner stay {StayId} not processed: Features:AgencyTier is off", stayId);
            return;
        }

        await stays.ProcessAsync(stayId, cancellationToken);
    }

    [DisableConcurrentExecution(LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.AgencyTier))
        {
            logger.LogInformation("Owner portal operations not run: Features:AgencyTier is off");
            return;
        }

        // The two kinds of work never hold each other: the stays (the automatic approvals, the blocks to give back, the sync results) run even when the expiry alerts
        // of the documents failed, and the alerts run even when a stay failed. Nothing is swallowed: every failure is logged and the run ends in error once both were
        // done, so the next run tries again.
        var failures = new List<Exception>();
        try
        {
            var alerts = await collaboration.RaiseExpiryAlertsAsync(cancellationToken);
            logger.LogInformation("Owner documents: {Raised} expiry To do raised, {Closed} closed", alerts.Raised, alerts.Closed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Owner portal operations: the expiry alerts of the documents failed; the owner stays are still looked after");
            failures.Add(ex);
        }

        try
        {
            var stayRun = await stays.ReconcileAsync(cancellationToken);
            logger.LogInformation(
                "Owner stays: {Approved} approved automatically, {Released} blocks given back, {SyncChanges} sync results, {Raised} To do raised, {Closed} closed",
                stayRun.Approved, stayRun.Released, stayRun.SyncChanges, stayRun.TodosRaised, stayRun.TodosClosed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Owner portal operations: the work on the owner stays failed");
            failures.Add(ex);
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("The background work of the owner portal operations failed in the expiry alerts and in the owner stays.", failures);
    }
}
