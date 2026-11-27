using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The sync of the connections of the active integrations (E09/F03/US01/T01, NF-57, ADR-023-R8): every 30 minutes it reads the status of the connections of every org that
/// has a channel, a tool or a function of the license on, brings the activations in line with what Octorate says, reconciles the license modules, reads the quota once for each
/// org and raises or closes the «To do» items. Only while <c>Features:OctorateConnector</c> is on (<see cref="RecurringJobsRegistration"/> removes the schedule of an earlier
/// deploy when it is off, and the run checks the flag again). All the decisions are in <see cref="IMarketplaceHealthService"/>; this only goes org by org.
/// </summary>
/// <remarks>
/// <para><b>Stops cleanly.</b> The first <see cref="OctorateRateLimitException"/> ends the run: nothing more is asked of Octorate, what was done is saved, and the rest is left to the
/// next run, which starts from the orgs that were synced the longest ago (<see cref="IMarketplaceHealthService.ListOrgsToSyncAsync"/>), so a run that was stopped is continued, not
/// repeated. Any other failure of an org is logged with the type of the error (never its message) and the next org goes on. No retry by Hangfire: the next run is 30 minutes away
/// and reads the same things.</para>
/// <para>The lock waits <see cref="JobLockTimeouts.DefaultSeconds"/>: the run is short and its next occurrence comes in 30 minutes anyway.</para>
/// </remarks>
public class MarketplaceConnectionSyncJob(
    IMarketplaceHealthService health,
    IFeatureFlags featureFlags,
    ILogger<MarketplaceConnectionSyncJob> logger)
{
    public const string RecurringJobId = "marketplace-connection-sync";

    /// <summary>Every 30 minutes, at minute 0 and 30 (UTC).</summary>
    public const string Cron = "*/30 * * * *";

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        var orgs = await health.ListOrgsToSyncAsync(cancellationToken);
        var totals = MarketplaceSyncResult.Empty;
        var orgsFailed = 0;
        var stoppedByRateLimit = false;

        foreach (var orgId in orgs)
        {
            try
            {
                totals = totals.Plus(await health.SyncAsync(orgId, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OctorateRateLimitException)
            {
                // The rate limit stops the run: the orgs not reached are the first of the next run.
                stoppedByRateLimit = true;
                logger.LogWarning(
                    "Marketplace connection sync stopped by the rate limit at org {OrgId}: the rest of the {Orgs} orgs is left to the next run", orgId, orgs.Count);
                break;
            }
            catch (Exception ex)
            {
                orgsFailed++;
                logger.LogWarning(
                    "Marketplace connection sync of org {OrgId} failed ({Error}): the next run reads it again", orgId, ex.GetType().Name);
            }
        }

        logger.LogInformation(
            "Marketplace connection sync: {Orgs} orgs, {Properties} properties read, {Updated} activations changed, {Unrecognised} unrecognised statuses, {Skipped} skipped, {Failed} failed, {OrgsFailed} orgs failed, {TodoRaised} To do raised, {TodoClosed} closed, stopped by the rate limit: {Stopped}",
            orgs.Count,
            totals.Properties,
            totals.Updated,
            totals.Unrecognised,
            totals.Skipped,
            totals.Failed,
            orgsFailed,
            totals.TodoRaised,
            totals.TodoClosed,
            stoppedByRateLimit);
    }
}
