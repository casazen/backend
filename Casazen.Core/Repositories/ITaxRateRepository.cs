using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ITaxRateRepository
{
    Task<TaxRate?> GetActiveRateForCityAsync(string city, DateTime date);
    Task<IEnumerable<TaxRate>> GetAllAsync();
    Task<TaxRate> AddAsync(TaxRate taxRate);
    Task<TaxRate> UpdateAsync(TaxRate taxRate);
}
