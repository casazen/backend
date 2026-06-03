using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class PricingHistoryRepository(AppDbContext context) : IPricingHistoryRepository
{
    public async Task<PricingHistory?> GetByIdAsync(Guid id)
    {
        return await context.PricingHistories.FindAsync(id);
    }

    public async Task<IEnumerable<PricingHistory>> GetByPropertyIdAsync(Guid propertyId)
    {
        return await context.PricingHistories
            .Where(h => h.PropertyId == propertyId)
            .OrderByDescending(h => h.AdaptationDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<PricingHistory>> GetByPropertyIdAndDateRangeAsync(
        Guid propertyId,
        DateTime startDate,
        DateTime endDate)
    {
        return await context.PricingHistories
            .Where(h => h.PropertyId == propertyId &&
                       h.AdaptationDate >= startDate &&
                       h.AdaptationDate <= endDate)
            .OrderByDescending(h => h.AdaptationDate)
            .ToListAsync();
    }

    public async Task<PricingHistory> AddAsync(PricingHistory history)
    {
        context.PricingHistories.Add(history);
        await context.SaveChangesAsync();
        return history;
    }

    public async Task UpdateAsync(PricingHistory history)
    {
        context.PricingHistories.Update(history);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var history = await GetByIdAsync(id);
        if (history != null)
        {
            context.PricingHistories.Remove(history);
            await context.SaveChangesAsync();
        }
    }
}
