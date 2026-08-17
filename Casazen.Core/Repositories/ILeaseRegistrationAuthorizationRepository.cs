using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ILeaseRegistrationAuthorizationRepository
{
    Task<LeaseRegistrationAuthorization?> GetByLeaseIdAsync(Guid leaseContractId);
    Task<LeaseRegistrationAuthorization> AddAsync(LeaseRegistrationAuthorization authorization);
}
