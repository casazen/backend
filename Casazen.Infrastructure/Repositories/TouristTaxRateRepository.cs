using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class TouristTaxRateRepository(AppDbContext context) : ITouristTaxRateRepository
{
    public async Task<TouristTaxRate?> GetByIdAsync(Guid id)
    {
        return await context.TouristTaxRates.FindAsync(id);
    }

    public async Task<IEnumerable<TouristTaxRate>> GetAllAsync()
    {
        return await context.TouristTaxRates
            .Where(t => t.IsActive)
            .OrderBy(t => t.City)
            .ToListAsync();
    }

    public async Task<TouristTaxRate?> GetActiveByCityAsync(string city, DateTime date)
    {
        return await context.TouristTaxRates
            .Where(t => t.City == city &&
                       t.IsActive &&
                       t.EffectiveFrom <= date &&
                       (t.EffectiveTo == null || t.EffectiveTo >= date))
            .OrderByDescending(t => t.EffectiveFrom)
            .FirstOrDefaultAsync();
    }

    public async Task<TouristTaxRate> AddAsync(TouristTaxRate taxRate)
    {
        context.TouristTaxRates.Add(taxRate);
        await context.SaveChangesAsync();
        return taxRate;
    }

    public async Task UpdateAsync(TouristTaxRate taxRate)
    {
        taxRate.UpdatedAt = DateTime.UtcNow;
        context.TouristTaxRates.Update(taxRate);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var taxRate = await GetByIdAsync(id);
        if (taxRate != null)
        {
            context.TouristTaxRates.Remove(taxRate);
            await context.SaveChangesAsync();
        }
    }
}
