using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class OtaSyncLogRepository(AppDbContext context) : IOtaSyncLogRepository
{
    public async Task<OtaSyncLog> CreateAsync(OtaSyncLog log)
    {
        context.OtaSyncLogs.Add(log);
        await context.SaveChangesAsync();
        return log;
    }

    public async Task<OtaSyncLog> UpdateAsync(OtaSyncLog log)
    {
        context.OtaSyncLogs.Update(log);
        await context.SaveChangesAsync();
        return log;
    }

    public async Task<IEnumerable<OtaSyncLog>> GetByPropertyAsync(Guid propertyId, int limit = 50)
    {
        return await context.OtaSyncLogs
            .Where(s => s.PropertyId == propertyId)
            .OrderByDescending(s => s.SyncStartedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<OtaSyncLog?> GetLatestByPropertyAndPlatformAsync(Guid propertyId, string platform)
    {
        return await context.OtaSyncLogs
            .Where(s => s.PropertyId == propertyId && s.Platform == platform)
            .OrderByDescending(s => s.SyncStartedAt)
            .FirstOrDefaultAsync();
    }
}
