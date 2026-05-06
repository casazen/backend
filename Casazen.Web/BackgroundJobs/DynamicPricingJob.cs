using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Background job for computing and applying dynamic pricing adapations daily at 02:00 UTC.
/// Iterates over all properties with enabled pricing configs, computes prices for next 90 days,
/// records PricingHistory entries, and delegates OTA sync to IOtaManager.
/// Per-property error isolation ensures one failure does not abort the batch.
/// </summary>
public class DynamicPricingJob
{
    private readonly IPricingAdapterConfigRepository _configRepository;
    private readonly IPricingHistoryRepository _historyRepository;
    private readonly IPricingAdapterService _pricingService;
    private readonly IOtaManager _otaManager;
    private readonly ILogger<DynamicPricingJob> _logger;
    private const int PRICING_WINDOW_DAYS = 90;

    public DynamicPricingJob(
        IPricingAdapterConfigRepository configRepository,
        IPricingHistoryRepository historyRepository,
        IPricingAdapterService pricingService,
        IOtaManager otaManager,
        ILogger<DynamicPricingJob> logger)
    {
        _configRepository = configRepository;
        _historyRepository = historyRepository;
        _pricingService = pricingService;
        _otaManager = otaManager;
        _logger = logger;
    }

    /// <summary>
    /// Main entry point for the recurring job. Called daily at 02:00 UTC by Hangfire.
    /// </summary>
    public async Task ExecuteAsync()
    {
        _logger.LogInformation("Starting DynamicPricingJob at {Timestamp}", DateTime.UtcNow);
        var startTime = DateTime.UtcNow;
        var processedCount = 0;
        var failedCount = 0;

        try
        {
            var enabledConfigs = await _configRepository.GetEnabledConfigsAsync();
            var configList = enabledConfigs.ToList();
            _logger.LogInformation("Found {ConfigCount} enabled pricing configs to process", configList.Count);

            foreach (var config in configList)
            {
                try
                {
                    await ProcessPropertyAsync(config);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogError(
                        ex,
                        "Error processing property {PropertyId}, continuing with next property",
                        config.PropertyId);
                }
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "DynamicPricingJob completed in {Duration}ms. Processed: {ProcessedCount}, Failed: {FailedCount}",
                duration.TotalMilliseconds, processedCount, failedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in DynamicPricingJob");
            throw;
        }
    }

    /// <summary>
    /// Process a single property: compute prices for next 90 days, record history, trigger OTA sync.
    /// </summary>
    private async Task ProcessPropertyAsync(Core.Entities.PricingAdapterConfig config)
    {
        _logger.LogInformation("Processing property {PropertyId}", config.PropertyId);

        var startDate = DateTime.UtcNow;
        var endDate = startDate.AddDays(PRICING_WINDOW_DAYS);

        // Compute pricing multipliers for each day in the window
        var dailyMultipliers = await ComputeDailyMultipliersAsync(startDate, endDate, config);

        // Record PricingHistory entries for adapted dates
        await RecordPricingHistoryAsync(config.PropertyId, dailyMultipliers);

        // Update config timestamps
        config.LastAdaptedAt = DateTime.UtcNow;
        config.NextScheduledRunAt = DateTime.UtcNow.AddDays(1);
        await _configRepository.UpdateAsync(config);

        _logger.LogInformation(
            "Updated config timestamps for property {PropertyId}: LastAdaptedAt={LastAdaptedAt}",
            config.PropertyId, config.LastAdaptedAt);

        // Delegate OTA sync to IOtaManager
        try
        {
            var syncSuccess = await _otaManager.SyncAllAsync(config.PropertyId);
            if (!syncSuccess)
            {
                _logger.LogWarning(
                    "OTA sync completed with errors for property {PropertyId}",
                    config.PropertyId);
            }
            else
            {
                _logger.LogInformation(
                    "OTA sync completed successfully for property {PropertyId}",
                    config.PropertyId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "OTA sync failed for property {PropertyId}, but pricing history was recorded",
                config.PropertyId);
        }
    }

    /// <summary>
    /// Compute pricing multipliers for each day in the range.
    /// Returns a dictionary mapping dates to multiplier values.
    /// </summary>
    private async Task<Dictionary<DateTime, decimal>> ComputeDailyMultipliersAsync(
        DateTime startDate,
        DateTime endDate,
        Core.Entities.PricingAdapterConfig config)
    {
        var multipliers = new Dictionary<DateTime, decimal>();
        var currentDate = startDate.Date;

        while (currentDate <= endDate.Date)
        {
            var multiplier = await _pricingService.CalculatePricingMultiplierAsync(
                currentDate,
                config.IncludeSeasonality,
                config.IncludePublicHolidays);

            multipliers[currentDate] = multiplier;
            currentDate = currentDate.AddDays(1);
        }

        _logger.LogInformation(
            "Computed {DayCount} daily pricing multipliers for property {PropertyId}",
            multipliers.Count, config.PropertyId);

        return multipliers;
    }

    /// <summary>
    /// Record PricingHistory entries for each adapted date.
    /// For now, uses placeholder pricing (would be enhanced to use actual property base price).
    /// </summary>
    private async Task RecordPricingHistoryAsync(
        Guid propertyId,
        Dictionary<DateTime, decimal> dailyMultipliers)
    {
        foreach (var kvp in dailyMultipliers)
        {
            var date = kvp.Key;
            var multiplier = kvp.Value;

            // Placeholder: use a base price of 100.0m for demonstration
            // In production, fetch the actual property base price from the Property entity
            var basePrice = 100.0m;
            var newPrice = basePrice * multiplier;

            var history = new Core.Entities.PricingHistory
            {
                PropertyId = propertyId,
                AdaptationDate = date,
                PreviousPrice = basePrice,
                NewPrice = newPrice,
                ChangeReason = $"Dynamic pricing adaptation (multiplier: {multiplier:F2}x)",
                AiConfidence = 0.85m,
                OtasSynced = string.Empty,
                SyncStatus = "Pending",
                CreatedAt = DateTime.UtcNow
            };

            await _historyRepository.AddAsync(history);
        }

        _logger.LogInformation(
            "Recorded {HistoryCount} pricing history entries for property {PropertyId}",
            dailyMultipliers.Count, propertyId);
    }
}
