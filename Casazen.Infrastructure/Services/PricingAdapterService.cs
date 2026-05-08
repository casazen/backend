using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PricingAdapterService(
    IPricingAdapterConfigRepository configRepository,
    IPricingHistoryRepository historyRepository,
    IPublicHolidayService publicHolidayService,
    ILogger<PricingAdapterService> logger) : IPricingAdapterService
{
    public async Task<decimal> CalculatePricingMultiplierAsync(
        DateTime date,
        bool includeSeasonality,
        bool includePublicHolidays)
    {
        var multiplier = 1.0m;

        // Check for public holiday surcharge
        if (includePublicHolidays)
        {
            var isHoliday = await publicHolidayService.IsPublicHolidayAsync(date);
            if (isHoliday)
            {
                multiplier *= 1.5m; // 50% surcharge for public holidays
                logger.LogInformation("Applied public holiday multiplier for date {Date}", date);
            }
        }

        // Apply seasonality multiplier
        if (includeSeasonality)
        {
            var seasonalMultiplier = GetSeasonalMultiplier(date);
            multiplier *= seasonalMultiplier;
        }

        return multiplier;
    }

    public async Task<PricingAdapterConfig?> GetConfigAsync(Guid propertyId)
    {
        return await configRepository.GetByPropertyIdAsync(propertyId);
    }

    public async Task<PricingAdapterConfig> SaveConfigAsync(PricingAdapterConfig config)
    {
        if (config.Id == Guid.Empty)
        {
            return await configRepository.AddAsync(config);
        }
        else
        {
            await configRepository.UpdateAsync(config);
            return config;
        }
    }

    public async Task<PricingHistory> RecordPricingChangeAsync(
        Guid propertyId,
        decimal previousPrice,
        decimal newPrice,
        string changeReason,
        decimal aiConfidence,
        string? otasSynced = null,
        string? syncStatus = null)
    {
        var history = new PricingHistory
        {
            PropertyId = propertyId,
            AdaptationDate = DateTime.UtcNow,
            PreviousPrice = previousPrice,
            NewPrice = newPrice,
            ChangeReason = changeReason,
            AiConfidence = aiConfidence,
            OtasSynced = otasSynced ?? string.Empty,
            SyncStatus = syncStatus ?? "Pending"
        };

        var result = await historyRepository.AddAsync(history);
        logger.LogInformation(
            "Recorded pricing change for property {PropertyId}: €{PreviousPrice} → €{NewPrice} " +
            "(reason: {Reason}, confidence: {Confidence:P})",
            propertyId, previousPrice, newPrice, changeReason, aiConfidence);

        return result;
    }

    /// <summary>
    /// Calculate seasonal multiplier based on month.
    /// High season (June-August): 1.3x
    /// Low season (November-February): 0.8x
    /// Shoulder season (other months): 1.0x
    /// </summary>
    private static decimal GetSeasonalMultiplier(DateTime date)
    {
        var month = date.Month;

        return month switch
        {
            // High season (summer holidays) - June through August
            6 or 7 or 8 => 1.3m,
            // Low season (winter) - November through February
            11 or 12 or 1 or 2 => 0.8m,
            // Shoulder season - March, April, May, September, October
            _ => 1.0m
        };
    }
}
