using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public interface ILeaseContractRepository
{
    Task<LeaseContract?> GetByIdAsync(Guid id);
    Task<LeaseContract?> GetByIdWithDetailsAsync(Guid id);
    Task<LeaseContract?> GetByExternalSigningSessionIdAsync(string externalSessionId);

    /// <summary>
    /// Lease list rows of <paramref name="scope"/> (its org and, when set, only the properties its owner owns),
    /// projected in SQL to <see cref="LeaseSummaryDto"/>: no party data is read. Newest first.
    /// </summary>
    Task<IReadOnlyList<LeaseSummaryDto>> GetSummariesAsync(HostScope scope, Guid? propertyId = null);

    Task<IEnumerable<LeaseContract>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<LeaseContract>> GetByStatusAsync(LeaseStatus status);
    Task<LeaseContract> AddAsync(LeaseContract lease);
    Task<LeaseContract> UpdateAsync(LeaseContract lease);
}
