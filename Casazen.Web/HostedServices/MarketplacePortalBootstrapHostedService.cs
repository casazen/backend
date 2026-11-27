using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;

namespace Casazen.Web.HostedServices;

/// <summary>
/// Fills the cache of Octorate's portal catalog at once when a start finds it empty (E09/F01/US01/T02, NF-54). The cache is written only by the daily
/// <see cref="MarketplacePortalRefreshJob"/> (03:30 UTC), which has no run of its own at startup: on a new environment, or a database that no refresh ever
/// reached, the marketplace and the wizard «Collega i tuoi canali» would list no portal until the first night. With <c>Features:OctorateConnector</c> on and
/// no portal listed, this queues <b>one run of that same job</b> on Hangfire and goes away.
/// </summary>
/// <remarks>
/// <para><b>Never blocks the start.</b> The work begins after the first <c>await</c> (<see cref="Task.Yield"/>), so the host is up before the database is
/// read, and what is queued is only a job: the call to Octorate runs on a Hangfire worker, not here. Any failure (a database or a Hangfire storage that is
/// not ready) is logged as a warning and swallowed: the daily job still fills the cache, and the start is never stopped for it.</para>
/// <para><b>Idempotent.</b> Nothing is queued while a portal is listed, so a restart of a filled cache queues nothing. The queued run is the job itself
/// (<see cref="MarketplacePortalRefreshJob"/>): it upserts the portals by code, so a second run, from another instance that started at the same moment or
/// from the daily schedule, finds the same rows and changes nothing; <c>[DisableConcurrentExecution]</c> keeps two runs from overlapping.</para>
/// <para>Registered only where Hangfire is (<c>Program.cs</c>): without it there is nothing to queue on.</para>
/// </remarks>
public sealed class MarketplacePortalBootstrapHostedService(
    IServiceScopeFactory scopes,
    ILogger<MarketplacePortalBootstrapHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await QueueRefreshWhenCacheIsEmptyAsync(stoppingToken);
    }

    /// <summary>
    /// Queues one run of the daily refresh when the connector is on and the cache lists no portal; otherwise does nothing. Never throws (but for a
    /// cancellation): a failure is logged as a warning. Returns whether a run was queued.
    /// </summary>
    public async Task<bool> QueueRefreshWhenCacheIsEmptyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;

            if (!services.GetRequiredService<IFeatureFlags>().IsEnabled(FeatureFlags.OctorateConnector))
                return false;

            if (await services.GetRequiredService<IMarketplacePortalCatalogService>().HasListedPortalsAsync(ct))
                return false;

            var jobId = services.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<MarketplacePortalRefreshJob>(job => job.ExecuteAsync(CancellationToken.None));
            logger.LogInformation("The cache of the marketplace portals is empty: a refresh was queued ({JobId}); the daily run is not waited for", jobId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The cache of the marketplace portals could not be checked or its refresh queued: the daily refresh will fill it");
            return false;
        }
    }
}
