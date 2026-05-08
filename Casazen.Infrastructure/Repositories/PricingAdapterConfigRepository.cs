using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class PricingAdapterConfigRepository(AppDbContext context) : IPricingAdapterConfigRepository
{
    public async Task<PricingAdapterConfig?> GetByIdAsync(Guid id)
    {
        return await context.PricingAdapterConfigs.FindAsync(id);
    }

    public async Task<PricingAdapterConfig?> GetByPropertyIdAsync(Guid propertyId)
    {
        return await context.PricingAdapterConfigs
            .FirstOrDefaultAsync(c => c.PropertyId == propertyId);
    }

    public async Task<IEnumerable<PricingAdapterConfig>> GetEnabledConfigsAsync()
    {
        return await context.PricingAdapterConfigs
            .Where(c => c.IsEnabled)
            .ToListAsync();
    }

    public async Task<IEnumerable<PricingAdapterConfig>> GetConfigsDueForAdaptationAsync()
    {
        return await context.PricingAdapterConfigs
            .Where(c => c.IsEnabled &&
                       (c.NextScheduledRunAt == null || c.NextScheduledRunAt <= DateTime.UtcNow))
            .ToListAsync();
    }

    public async Task<PricingAdapterConfig> AddAsync(PricingAdapterConfig config)
    {
        context.PricingAdapterConfigs.Add(config);
        await context.SaveChangesAsync();
        return config;
    }

    public async Task UpdateAsync(PricingAdapterConfig config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        context.PricingAdapterConfigs.Update(config);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var config = await GetByIdAsync(id);
        if (config != null)
        {
            context.PricingAdapterConfigs.Remove(config);
            await context.SaveChangesAsync();
        }
    }
}
