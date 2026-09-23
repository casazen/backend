using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public interface ILeaseRegistrationRepository
{
    Task<LeaseRegistration?> GetByLeaseIdAsync(Guid leaseContractId);
    Task<IEnumerable<LeaseRegistration>> GetByStatusAsync(RegistrationStatus status);
    Task<bool> TryReserveSubmissionAsync(LeaseRegistration registration);

    /// <summary>
    /// Atomically claims a <see cref="RegistrationStatus.Failed"/> registration for a retry (Failed -> Pending).
    /// Returns false when another request already claimed it or it is no longer Failed.
    /// </summary>
    Task<bool> TryReserveRetryAsync(LeaseRegistration failedRegistration);
    Task<LeaseRegistration> AddAsync(LeaseRegistration registration);
    Task<LeaseRegistration> UpdateAsync(LeaseRegistration registration);
}
