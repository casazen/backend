using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily refresh of the cache of Octorate's portal catalog (E09/F01/US01/T02, NF-54): the marketplace reads the portals from CasaZen's own
/// database and never calls Octorate while a page loads. All the decisions are in <see cref="IMarketplacePortalCatalogService"/>; this only
/// runs it. Registered only while <c>Features:OctorateConnector</c> is on (<see cref="RecurringJobsRegistration"/>), which also removes the
/// schedule of an earlier deploy when it is off. An error of Octorate leaves the cache as it was and the run is retried a few times.
/// </summary>
public class MarketplacePortalRefreshJob(IMarketplacePortalCatalogService portals)
{
    public const string RecurringJobId = "marketplace-portal-refresh";

    /// <summary>Every day at 03:30 UTC.</summary>
    public const string Cron = "30 3 * * *";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) => await portals.RefreshAsync(cancellationToken);
}
