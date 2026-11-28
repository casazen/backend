using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

public interface ILeaseWorkflowService
{
    Task<LeaseContract> CreateDraftAsync(Guid propertyId, CreateLeaseRequest request);

    Task<IReadOnlyList<LeaseSummaryDto>> GetLeasesAsync(HostScope scope, Guid? propertyId = null);

    /// <summary>
    /// The lease list of <paramref name="scope"/> narrowed by a view and a search text (<see cref="LeaseListQuery"/>), each
    /// row with the first tenant and the state of the rent (LR-01, B3). The views, the overdue installments and the
    /// registration deadline are resolved on today's date in Europe/Rome.
    /// </summary>
    Task<IReadOnlyList<LeaseSummaryDto>> GetLeasesAsync(HostScope scope, LeaseListQuery query);

    Task<LeaseContract?> GetLeaseDetailAsync(Guid leaseId);
}

public record CreateLeaseRequest(
    FiscalRegime? FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    IEnumerable<CreatePartyRequest> Parties,
    RentBandCharacteristics? CanoneConcordatoCharacteristics = null)
{
    public LeaseContractType? ContractType { get; init; }

    public LeaseTaxRegime? TaxRegime { get; init; }

    public decimal? SecurityDeposit { get; init; }
}

public record CreatePartyRequest(
    PartyRole Role,
    string FirstName,
    string LastName,
    string FiscalCode,
    string Citizenship,
    string ContactEmail,
    string? ContactPhone = null);
