using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public class SeoContentRefreshJob(ISeoContentService seoContentService, ILogger<SeoContentRefreshJob> logger)
{
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        logger.LogInformation("Starting monthly SEO content refresh");
        var refreshed = await seoContentService.RefreshStalePagesAsync(CancellationToken.None);
        logger.LogInformation("SEO content refresh completed: {RefreshedCount} pages refreshed", refreshed);
    }
}
