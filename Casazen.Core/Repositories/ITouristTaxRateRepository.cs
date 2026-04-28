using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ITouristTaxRateRepository
{
    Task<TouristTaxRate?> GetByIdAsync(Guid id);
    Task<IEnumerable<TouristTaxRate>> GetAllAsync();
    Task<TouristTaxRate?> GetActiveByCityAsync(string city, DateTime date);
    Task<TouristTaxRate> AddAsync(TouristTaxRate taxRate);
    Task UpdateAsync(TouristTaxRate taxRate);
    Task DeleteAsync(Guid id);
}
