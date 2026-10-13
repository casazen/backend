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

    public async Task<IReadOnlyList<TouristTaxRate>> GetActiveInPeriodAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        // One day of margin on both sides: the calculator checks every night on the Europe/Rome calendar date.
        var fromBound = from.AddDays(-1);
        var toBound = to.AddDays(1);
        return await context.TouristTaxRates
            .AsNoTracking()
            .Where(t => t.IsActive
                        && t.EffectiveFrom <= toBound
                        && (t.EffectiveTo == null || t.EffectiveTo >= fromBound))
            .OrderBy(t => t.City)
            .ThenBy(t => t.EffectiveFrom)
            .ToListAsync(cancellationToken);
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
