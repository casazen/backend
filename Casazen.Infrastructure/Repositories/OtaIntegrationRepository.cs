using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class OtaIntegrationRepository(AppDbContext context) : IOtaIntegrationRepository
{
    public async Task<IEnumerable<OtaIntegration>> GetByPropertyIdAsync(Guid propertyId)
    {
        return await context.OtaIntegrations
            .Where(o => o.PropertyId == propertyId)
            .OrderBy(o => o.Platform)
            .ToListAsync();
    }

    public async Task<OtaIntegration?> GetByIdAsync(Guid id)
    {
        return await context.OtaIntegrations.FindAsync(id);
    }

    public async Task<OtaIntegration> CreateAsync(OtaIntegration integration)
    {
        context.OtaIntegrations.Add(integration);
        await context.SaveChangesAsync();
        return integration;
    }

    public async Task UpdateAsync(OtaIntegration integration)
    {
        context.OtaIntegrations.Update(integration);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var integration = await GetByIdAsync(id);
        if (integration != null)
        {
            context.OtaIntegrations.Remove(integration);
            await context.SaveChangesAsync();
        }
    }
}
