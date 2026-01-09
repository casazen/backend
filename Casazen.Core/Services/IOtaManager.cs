namespace Casazen.Core.Services;

public interface IOtaManager
{
    Task<bool> SyncAllAsync(Guid propertyId);
    Task<bool> SyncPlatformAsync(string platform, string externalId);
    Task<bool> UpdatePricingAsync(Guid propertyId, decimal newPrice);
    Task<bool> PullBookingsAsync(Guid propertyId);
    Task<Dictionary<string, bool>> GetSyncStatusAsync(Guid propertyId);
}