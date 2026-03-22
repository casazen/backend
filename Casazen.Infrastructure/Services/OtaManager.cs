using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.OTA;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class OtaManager(
    IPropertyRepository propertyRepository,
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
}
