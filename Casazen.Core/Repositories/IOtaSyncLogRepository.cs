using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IOtaSyncLogRepository
{
    Task<OtaSyncLog> CreateAsync(OtaSyncLog log);
    Task<OtaSyncLog> UpdateAsync(OtaSyncLog log);
    Task<IEnumerable<OtaSyncLog>> GetByPropertyAsync(Guid propertyId, int limit = 50);
    Task<OtaSyncLog?> GetLatestByPropertyAndPlatformAsync(Guid propertyId, string platform);
}
