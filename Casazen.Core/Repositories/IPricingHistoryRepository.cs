using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPricingHistoryRepository
{
    Task<PricingHistory?> GetByIdAsync(Guid id);
    Task<IEnumerable<PricingHistory>> GetByPropertyIdAsync(Guid propertyId);
    Task<PricingHistory> AddAsync(PricingHistory history);
    Task UpdateAsync(PricingHistory history);
    Task DeleteAsync(Guid id);
}
