using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly retention of guest data per category (CO-15, docs/runbooks/gdpr.md): <see cref="IGuestDataRetentionService"/>
/// applies each configured <c>Gdpr:Retention:*</c> period to every org and skips, with a warning, the categories without
/// a period and a cited source. Idempotent: a second run the same night changes nothing.
/// </summary>
public class GdprDataRetentionJob(IGuestDataRetentionService retentionService, ILogger<GdprDataRetentionJob> logger)
{
    public const string RecurringJobId = "gdpr-data-retention";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var result = await retentionService.ApplyAsync();
        logger.LogInformation(
            "GDPR retention job done: {Configured} of {Total} categories configured",
            result.Categories.Count(c => c.Configured),
            result.Categories.Count);
    }
}
