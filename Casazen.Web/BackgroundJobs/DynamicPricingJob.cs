using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly job of the seasonal price suggestions ("Suggerimenti stagionali", D4, PC-15), at 02:00 UTC. For every enabled
/// property it asks <see cref="IPricingAdapterService.RegenerateSuggestionsAsync"/> to recompute the suggestions when they
/// are due by the configured frequency, compared by Rome calendar dates (A2-34): a run a few minutes earlier or later
/// never skips a day, a weekly schedule runs once a week and a never-computed property is processed at the first run.
/// The computation upserts one row per date (no growing history) and never pushes prices anywhere.
/// Per-property error isolation ensures one failure does not abort the batch.
/// </summary>
public class DynamicPricingJob(
    IPricingAdapterConfigRepository configRepository,
    IPricingAdapterService pricingService,
    ILogger<DynamicPricingJob> logger)
{
    public const string RecurringJobId = "dynamic-pricing-adaptation";

    [DisableConcurrentExecution("DynamicPricingJob", JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var configs = (await configRepository.GetEnabledConfigsAsync()).ToList();
        logger.LogInformation("Seasonal price suggestions: {ConfigCount} enabled properties", configs.Count);

        var computed = 0;
        var failed = 0;
        foreach (var config in configs)
        {
            try
            {
                var result = await pricingService.RegenerateSuggestionsAsync(config.PropertyId, onlyIfDue: true);
                if (result.Status == SeasonalSuggestionRunStatus.Computed)
                    computed++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(
                    ex,
                    "Seasonal price suggestions failed for property {PropertyId}, continuing with next property",
                    config.PropertyId);
            }
        }

        logger.LogInformation(
            "Seasonal price suggestions completed. Computed: {ComputedCount}, Failed: {FailedCount}", computed, failed);
    }
}
