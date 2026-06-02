using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public interface ILeaseContractRepository
{
    Task<LeaseContract?> GetByIdAsync(Guid id);
    Task<LeaseContract?> GetByIdWithDetailsAsync(Guid id);
    Task<LeaseContract?> GetByExternalSigningSessionIdAsync(string externalSessionId);
    Task<IEnumerable<LeaseContract>> GetByOwnerAsync(string ownerId, Guid? propertyId = null);
    Task<IEnumerable<LeaseContract>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<LeaseContract>> GetByStatusAsync(LeaseStatus status);
    Task<LeaseContract> AddAsync(LeaseContract lease);
    Task<LeaseContract> UpdateAsync(LeaseContract lease);
}
