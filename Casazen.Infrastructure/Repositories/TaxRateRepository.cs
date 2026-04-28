using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class TaxRateRepository(AppDbContext context) : ITaxRateRepository
{
    public async Task<TaxRate?> GetActiveRateForCityAsync(string city, DateTime date)
    {
        return await context.TaxRates
            .Where(t => t.City.ToLower() == city.ToLower()
                && t.EffectiveFrom <= date
                && (t.EffectiveTo == null || t.EffectiveTo >= date))
            .OrderByDescending(t => t.EffectiveFrom)
            .FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<TaxRate>> GetAllAsync()
    {
        return await context.TaxRates
            .OrderBy(t => t.Region)
            .ThenBy(t => t.City)
            .ToListAsync();
    }

    public async Task<TaxRate> AddAsync(TaxRate taxRate)
    {
        context.TaxRates.Add(taxRate);
        await context.SaveChangesAsync();
        return taxRate;
    }

    public async Task<TaxRate> UpdateAsync(TaxRate taxRate)
    {
        taxRate.UpdatedAt = DateTime.UtcNow;
        context.TaxRates.Update(taxRate);
        await context.SaveChangesAsync();
        return taxRate;
    }
}
