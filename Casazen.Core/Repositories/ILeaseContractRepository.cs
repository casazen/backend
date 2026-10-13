using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Repositories;

public interface ILeaseContractRepository
{
    Task<LeaseContract?> GetByIdAsync(Guid id);
    Task<LeaseContract?> GetByIdWithDetailsAsync(Guid id);

    /// <summary>
    /// Lease list rows of <paramref name="scope"/> (its org and, unless the scope is org-wide, only the properties it reaches),
    /// projected in SQL to <see cref="LeaseSummaryDto"/>: no party data is read. Newest first.
    /// </summary>
    Task<IReadOnlyList<LeaseSummaryDto>> GetSummariesAsync(HostScope scope, Guid? propertyId = null);

    /// <summary>
    /// The lease list of <paramref name="scope"/> narrowed by <paramref name="query"/> (a property, a
    /// <see cref="LeaseListView"/>, a text looked for in the property and in the tenants' names), each row with the name of
    /// its first tenant and what the rent ledger says (next installment, overdue). One SQL statement whatever the number of
    /// leases: the tenant name and the ledger are part of the projection, never read per row (LR-01, B3). Newest first.
    /// </summary>
    /// <param name="todayInRome">Today on the Rome calendar (midnight UTC): the views and the overdue installments depend on it.</param>
    Task<IReadOnlyList<LeaseSummaryDto>> GetSummariesAsync(HostScope scope, LeaseListQuery query, DateTime todayInRome);

    Task<IEnumerable<LeaseContract>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<LeaseContract>> GetByStatusAsync(LeaseStatus status);
    Task<LeaseContract> AddAsync(LeaseContract lease);
    Task<LeaseContract> UpdateAsync(LeaseContract lease);
}
