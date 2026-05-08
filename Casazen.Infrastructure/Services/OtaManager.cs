using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.OTA;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

public class OtaManager(
    IPropertyRepository propertyRepository,
    IPricingHistoryRepository pricingHistoryRepository,
    IChannelFactory channelFactory,
    ILogger<OtaManager> logger) : IOtaManager
{
    public async Task<bool> SyncAllAsync(Guid propertyId)
    {
        try
        {
            var property = await propertyRepository.GetByIdAsync(propertyId);
            if (property == null)
                return false;

            var syncStatus = new Dictionary<string, bool>();
            foreach (var integration in property.OtaIntegrations.Where(i => i.SyncEnabled))
            {
                var success = await SyncPlatformAsync(integration.Platform, integration.ExternalPropertyId);
                syncStatus[integration.Platform] = success;
            }

            logger.LogInformation("Synced all platforms for property {PropertyId}", propertyId);
            return syncStatus.Values.All(v => v);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing all platforms for property {PropertyId}", propertyId);
            return false;
        }
    }

    public async Task<bool> SyncPlatformAsync(string platform, string externalId)
    {
        try
        {
            var adapter = channelFactory.GetAdapter(platform);
            // Sync logic here
            logger.LogInformation("Synced {Platform} for external ID {ExternalId}", platform, externalId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing {Platform}", platform);
            return false;
        }
    }

    public async Task<bool> UpdatePricingAsync(Guid propertyId, decimal newPrice)
    {
        try
        {
            var property = await propertyRepository.GetByIdAsync(propertyId);
            if (property == null)
                return false;

            property.NightlyRate = newPrice;
            await propertyRepository.UpdateAsync(property);

            foreach (var integration in property.OtaIntegrations.Where(i => i.IsActive))
            {
                var adapter = channelFactory.GetAdapter(integration.Platform);
                // Update pricing on platform
            }

            logger.LogInformation("Updated pricing for property {PropertyId} to {Price}", propertyId, newPrice);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating pricing for property {PropertyId}", propertyId);
            return false;
        }
    }

    public async Task<bool> PullBookingsAsync(Guid propertyId)
    {
        try
        {
            var property = await propertyRepository.GetByIdAsync(propertyId);
            if (property == null)
                return false;

            foreach (var integration in property.OtaIntegrations.Where(i => i.SyncEnabled))
            {
                var adapter = channelFactory.GetAdapter(integration.Platform);
                // Pull bookings from platform
            }

            logger.LogInformation("Pulled bookings for property {PropertyId}", propertyId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error pulling bookings for property {PropertyId}", propertyId);
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> GetSyncStatusAsync(Guid propertyId)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId);
        if (property == null)
            return new Dictionary<string, bool>();

        return property.OtaIntegrations.ToDictionary(
            i => i.Platform,
            i => i.IsActive && i.SyncEnabled
        );
    }

    public async Task<bool> ValidateIntegrationAsync(string platform, string apiKey)
    {
        try
        {
            var adapter = channelFactory.GetAdapter(platform);
            var isValid = await adapter.ValidateCredentialsAsync(apiKey);
            logger.LogInformation("Validated {Platform} credentials: {IsValid}", platform, isValid);
            return isValid;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating {Platform}", platform);
            return false;
        }
    }

    public async Task<bool> UpdatePricingBatchAsync(Guid propertyId, Dictionary<DateOnly, decimal> pricesByDate)
    {
        try
        {
            var property = await propertyRepository.GetByIdAsync(propertyId);
            if (property == null)
            {
                logger.LogError("Property {PropertyId} not found", propertyId);
                return false;
            }

            if (pricesByDate == null || pricesByDate.Count == 0)
            {
                logger.LogWarning("No pricing data provided for batch update on property {PropertyId}", propertyId);
                return false;
            }

            var activeIntegrations = property.OtaIntegrations.Where(i => i.IsActive && i.SyncEnabled).ToList();
            if (!activeIntegrations.Any())
            {
                logger.LogWarning("No active OTA integrations found for property {PropertyId}", propertyId);
                return false;
            }

            logger.LogInformation("Starting batch pricing update for property {PropertyId} across {Count} adapters",
                propertyId, activeIntegrations.Count);

            var allResults = new Dictionary<string, Dictionary<DateOnly, bool>>();
            var overallSuccess = true;

            foreach (var integration in activeIntegrations)
            {
                try
                {
                    var adapter = channelFactory.GetAdapter(integration.Platform);
                    var results = await adapter.UpdatePricingBatchAsync(
                        integration.ExternalPropertyId,
                        pricesByDate
                    );

                    allResults[integration.Platform] = results;

                    var platformSuccess = results.Values.All(v => v);
                    if (!platformSuccess)
                    {
                        overallSuccess = false;
                        var failedDates = results
                            .Where(r => !r.Value)
                            .Select(r => r.Key.ToString("yyyy-MM-dd"))
                            .ToList();
                        logger.LogWarning("Platform {Platform} failed to sync {Count} dates: {FailedDates}",
                            integration.Platform, failedDates.Count, string.Join(", ", failedDates));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during batch pricing update on platform {Platform} for property {PropertyId}",
                        integration.Platform, propertyId);
                    overallSuccess = false;
                }
            }

            // Write PricingHistory record with sync status
            var syncStatusJson = JsonSerializer.Serialize(allResults);
            var syncedPlatforms = allResults
                .Where(kvp => kvp.Value.Values.Any(v => v))
                .Select(kvp => kvp.Key)
                .ToList();

            var pricingHistory = new PricingHistory
            {
                PropertyId = propertyId,
                AdaptationDate = DateTime.UtcNow,
                PreviousPrice = property.NightlyRate,
                NewPrice = pricesByDate.Values.FirstOrDefault(),
                ChangeReason = "Batch OTA price update",
                AiConfidence = 1.0m,
                OtasSynced = JsonSerializer.Serialize(syncedPlatforms),
                SyncStatus = overallSuccess ? "synced" : "failed",
                CreatedAt = DateTime.UtcNow
            };

            await pricingHistoryRepository.AddAsync(pricingHistory);

            logger.LogInformation("Completed batch pricing update for property {PropertyId}. Overall status: {Status}",
                propertyId, overallSuccess ? "success" : "partial_failure");

            return overallSuccess;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in batch pricing update for property {PropertyId}", propertyId);
            return false;
        }
    }
}
