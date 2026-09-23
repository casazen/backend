using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public interface ILeaseRegistrationRepository
{
    Task<LeaseRegistration?> GetByLeaseIdAsync(Guid leaseContractId);
    Task<IEnumerable<LeaseRegistration>> GetByStatusAsync(RegistrationStatus status);
    Task<bool> TryReserveSubmissionAsync(LeaseRegistration registration);
    Task<LeaseRegistration> AddAsync(LeaseRegistration registration);
    Task<LeaseRegistration> UpdateAsync(LeaseRegistration registration);
}
