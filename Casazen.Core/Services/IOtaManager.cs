namespace Casazen.Core.Services;

public interface IOtaManager
{
    Task<bool> SyncAllAsync(Guid propertyId);
    Task<bool> SyncPlatformAsync(string platform, string externalId);
    Task<bool> UpdatePricingAsync(Guid propertyId, decimal newPrice);
    Task<bool> ValidateIntegrationAsync(string platform, string apiKey);
    Task<bool> PullBookingsAsync(Guid propertyId);
    Task<Dictionary<string, bool>> GetSyncStatusAsync(Guid propertyId);

    /// <summary>
    /// Updates pricing across all active OTA adapters for a date range.
    /// Orchestrates batch updates and records PricingHistory with sync status.
    /// </summary>
    /// <param name="propertyId">The internal property ID</param>
    /// <param name="pricesByDate">Dictionary mapping DateOnly to price for each date</param>
    /// <returns>Overall success status (true if all adapters succeeded, false otherwise)</returns>
    Task<bool> UpdatePricingBatchAsync(Guid propertyId, Dictionary<DateOnly, decimal> pricesByDate);
}