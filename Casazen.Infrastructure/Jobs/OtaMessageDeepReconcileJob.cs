using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The deep pass over the OTA conversations, daily at 02:30 UTC (E05/F01/US01/T03, ADR-032, NF-62, OCT-09): for every org whose connection is
/// connected it reads again the threads that had activity since the last deep pass minus <see cref="Overlap"/>
/// (<see cref="IMessageSyncService.ReconcileDeepAsync"/>), so a notification that was lost, or that arrived after Octorate gave up retrying (two days),
/// still reaches CasaZen even if the pass of every 10 minutes did not see it. Behind <c>Features:OctorateConnector</c> (the registration removes it
/// while the flag is off, and a run with the flag off does nothing).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The window.</b> At least two days, the retry window of the webhook (OCT-09): consecutive windows overlap by at least that much, and the
/// service never narrows it. The checkpoint (<c>LastDeepPassAt</c>) is kept on the listing by the service and moves only when the listing was read.</item>
/// <item><b>Tenant.</b> A system read over every org; each org is read for itself under <see cref="IOctorateOrgScope.Use"/>.</item>
/// <item><b>Failures.</b> A failure of an org (a rate limit, an error of Octorate) was stored as a code on the checkpoints of its listing by the
/// service and never stops the others. No retry by Hangfire: tomorrow's pass reads the same window and more.</item>
/// <item>The lock waits <see cref="LockTimeoutSeconds"/>, the value of <c>JobLockTimeouts.DefaultSeconds</c> (a test keeps the two equal).</item>
/// </list>
/// </remarks>
public class OtaMessageDeepReconcileJob(
    AppDbContext db,
    IMessageSyncService sync,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    ILogger<OtaMessageDeepReconcileJob> logger)
{
    public const string RecurringJobId = "ota-message-deep-reconcile";

    /// <summary>Every day at 02:30 (UTC).</summary>
    public const string Cron = "30 2 * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.DefaultSeconds</c>, for the jobs that run once a day.</summary>
    public const int LockTimeoutSeconds = 300;

    /// <summary>The overlap of the windows: the retry window of the webhook of Octorate (OCT-09).</summary>
    public static readonly TimeSpan Overlap = MessageSyncService.MinDeepOverlap;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Octorate message deep reconciliation: the connector is off, nothing done");
            return;
        }

        var orgIds = await OtaMessageRuns.ListConnectedOrgsAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        int threads = 0, added = 0, rateLimited = 0, failed = 0;

        foreach (var orgId in orgIds)
        {
            try
            {
                SyncResult result;
                using (orgScope.Use(orgId))
                    result = await sync.ReconcileDeepAsync(orgId, Overlap, cancellationToken);

                threads += result.ThreadsSeen;
                added += result.MessagesAdded;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OctorateRateLimitException)
            {
                rateLimited++;
                logger.LogWarning("Octorate message deep reconciliation of org {OrgId} stopped: its calls to Octorate are used up", orgId);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(
                    "Octorate message deep reconciliation of org {OrgId} failed ({Error}): tomorrow's pass reads it again",
                    orgId,
                    ex is OctorateException octorate ? octorate.Code : ex.GetType().Name);
            }
        }

        logger.LogInformation(
            "Octorate message deep reconciliation: {Orgs} orgs, {Threads} threads read, {Added} messages added, {RateLimited} orgs stopped by the rate limit, {Failed} failed",
            orgIds.Count,
            threads,
            added,
            rateLimited,
            failed);
    }
}
