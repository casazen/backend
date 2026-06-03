using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPricingAdapterConfigRepository
{
    Task<PricingAdapterConfig?> GetByIdAsync(Guid id);
    Task<PricingAdapterConfig?> GetByPropertyIdAsync(Guid propertyId);
    Task<IEnumerable<PricingAdapterConfig>> GetEnabledConfigsAsync();
    Task<IEnumerable<PricingAdapterConfig>> GetConfigsDueForAdaptationAsync();
    Task<PricingAdapterConfig> AddAsync(PricingAdapterConfig config);
    Task UpdateAsync(PricingAdapterConfig config);
    Task DeleteAsync(Guid id);
}
