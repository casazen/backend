using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>Lease drafts and reads. The signature is <see cref="ILeaseSigningService"/> (LT-02), the RLI registration <see cref="IRliRegistrationService"/> (LT-01).</summary>
public interface ILeaseWorkflowService
{
    /// <summary>
    /// Creates a draft lease on <paramref name="propertyId"/>. No ownership check: the caller has authorized the
    /// property for <c>lease.create</c> (TN-3: the owner or an org-wide member of its org).
    /// </summary>
    Task<LeaseContract> CreateDraftAsync(Guid propertyId, CreateLeaseRequest request);

    /// <summary>Lease list of <paramref name="scope"/> (built by the web layer from the caller, TN-3).</summary>
    Task<IReadOnlyList<LeaseSummaryDto>> GetLeasesAsync(HostScope scope, Guid? propertyId = null);

    /// <summary>
    /// The lease with property, parties, registration and events, or <c>null</c> when it does not exist in the
    /// caller's org (tenant filter). No ownership check: the caller authorizes the returned row (TN-3).
    /// </summary>
    Task<LeaseContract?> GetLeaseDetailAsync(Guid leaseId);
}

public record CreateLeaseRequest(
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    IEnumerable<CreatePartyRequest> Parties,
    RentBandCharacteristics? CanoneConcordatoCharacteristics = null);

public record CreatePartyRequest(
    PartyRole Role,
    string FirstName,
    string LastName,
    string FiscalCode,
    string Citizenship,
    string ContactEmail);
