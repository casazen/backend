using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ILeaseWorkflowService
{
    Task<LeaseContract> CreateDraftAsync(Guid propertyId, string ownerId, CreateLeaseRequest request);
    Task<SigningInitiatedResult> InitiateSigningAsync(Guid leaseId, string ownerId);
    Task HandleESignEventAsync(string providerPayload);
    Task<LeaseRegistration> TriggerRegistrationAsync(Guid leaseId, string ownerId);
    Task<LeaseRegistration?> GetRegistrationAsync(Guid leaseId, string ownerId);
    Task<Stream> GetRegistrationReceiptAsync(Guid leaseId, string ownerId);
    Task<IEnumerable<LeaseContract>> GetOwnerLeasesAsync(string ownerId, Guid? propertyId = null);
    Task<LeaseContract?> GetLeaseDetailAsync(Guid leaseId, string ownerId);
}

public record CreateLeaseRequest(
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    IEnumerable<CreatePartyRequest> Parties);

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
