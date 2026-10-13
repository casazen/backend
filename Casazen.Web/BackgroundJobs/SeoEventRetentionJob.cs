using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly retention of the SEO funnel events (SE-04): deletes the events older than <c>Seo:Events:RetentionDays</c>
/// (docs/runbooks/seo-funnel.md). The events hold no personal data; the retention keeps the table small and the report
/// to a window that is still meaningful. Idempotent.
/// </summary>
public class SeoEventRetentionJob(ISeoEventService seoEventService, ILogger<SeoEventRetentionJob> logger)
{
    public const string RecurringJobId = "seo-event-retention";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var deleted = await seoEventService.PurgeExpiredAsync();
        logger.LogInformation("SEO event retention job done: {Deleted} events deleted", deleted);
    }
}
