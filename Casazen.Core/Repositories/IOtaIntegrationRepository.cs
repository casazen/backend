using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IOtaIntegrationRepository
{
    Task<IEnumerable<OtaIntegration>> GetByPropertyIdAsync(Guid propertyId);
    Task<OtaIntegration?> GetByIdAsync(Guid id);
    Task<OtaIntegration> CreateAsync(OtaIntegration integration);
    Task UpdateAsync(OtaIntegration integration);
    Task DeleteAsync(Guid id);
}
