using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public interface ILeaseContractRepository
{
    Task<LeaseContract?> GetByIdAsync(Guid id);
    Task<LeaseContract?> GetByIdWithDetailsAsync(Guid id);
    Task<IEnumerable<LeaseContract>> GetByOwnerAsync(string ownerId);
    Task<IEnumerable<LeaseContract>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<LeaseContract>> GetByStatusAsync(LeaseStatus status);
    Task<LeaseContract> AddAsync(LeaseContract lease);
    Task<LeaseContract> UpdateAsync(LeaseContract lease);
}
