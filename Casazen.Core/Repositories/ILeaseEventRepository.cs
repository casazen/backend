using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ILeaseEventRepository
{
    Task<IEnumerable<LeaseEvent>> GetByLeaseIdAsync(Guid leaseContractId);
    Task<LeaseEvent> AddAsync(LeaseEvent leaseEvent);
}
