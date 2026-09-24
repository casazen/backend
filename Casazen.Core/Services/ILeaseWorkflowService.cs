using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ILeaseWorkflowService
{
    Task<LeaseContract> CreateDraftAsync(Guid propertyId, string ownerId, CreateLeaseRequest request);
    Task<SigningInitiatedResult> InitiateSigningAsync(Guid leaseId, string ownerId);
    Task HandleESignEventAsync(string providerPayload);
    Task<LeaseRegistration> TriggerRegistrationAsync(
        Guid leaseId, string ownerId, RegistrationAuthorizationRequest authorization);
    Task<Stream> GetRegistrationReceiptAsync(Guid leaseId, string ownerId);

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

public record SigningInitiatedResult(
    Guid LeaseId,
    LeaseStatus Status,
    IEnumerable<SignerInfo> Signers);

public record SignerInfo(
    Guid PartyId,
    PartyRole Role,
    string Name,
    string SigningUrl,
    DateTime ExpiresAt);
