using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Core.DTOs.Leases;

// API contract of the long-term leases (LT-11, A7-17). The API never returns EF entities: a new column on
// LeaseContract, Party or Property must not reach the client by accident. Personal data of the parties is either
// omitted (list) or masked (detail); internal fields (owner id, storage paths, provider ids, event payloads, the
// property's safety checklist) are never part of these types.

/// <summary>The property a lease belongs to, as shown next to the lease.</summary>
public sealed record LeasePropertyDto(Guid Id, string Name, string City);

/// <summary>
/// Row of the lease list: no personal data of the parties, only how many they are. <c>RegistrationDeadline</c> is null
/// while it is to be determined (LT-04, <see cref="RliRegistrationDeadline.Resolve(LeaseStatus, DateTime?, DateTime, DateTime)"/>).
/// </summary>
public sealed record LeaseSummaryDto(
    Guid Id,
    Guid PropertyId,
    LeasePropertyDto? Property,
    LeaseStatus Status,
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    DateTime? StipulaDate,
    DateTime? RegistrationDeadline,
    int PartyCount,
    bool HasExtraEUTenant,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// A party of the lease as the host sees it: the name (needed to recognise the signer), the role and whether the
/// tenant is extra-EU (Questura notice). Fiscal code and email are masked; citizenship is not returned.
/// </summary>
public sealed record LeasePartyDto(
    Guid Id,
    PartyRole Role,
    string FirstName,
    string LastName,
    string FiscalCodeMasked,
    string ContactEmailMasked,
    bool IsExtraEU);

/// <summary>
/// RLI registration state (LT-01). The provider id, the receipt storage path and the declaring user stay on the server:
/// the client only learns whether a receipt can be downloaded.
/// </summary>
public sealed record LeaseRegistrationDto(
    RegistrationStatus Status,
    RegistrationChannel Channel,
    string? RegistrationCode,
    DateTime? RegistrationDate,
    DateTime? SubmittedAt,
    DateTime? ConfirmedAt,
    string? FailureCode,
    bool HasReceipt);

/// <summary>
/// A party as signer of the contract (LT-02, A7-16). <c>Method</c> Offline: signature on paper or with the party's own
/// digital signature, recorded when the landlord uploads the signed contract. <c>SigningUrl</c>: the provider link, only
/// while the signature is pending and only for a caller who may sign the lease; <c>SigningUrlExpired</c> is computed
/// at read time.
/// </summary>
public sealed record LeaseSignerDto(
    Guid PartyId,
    PartyRole Role,
    string FirstName,
    string LastName,
    LeaseSignatureMethod Method,
    LeaseSignerStatus Status,
    string? SigningUrl,
    DateTime? SigningUrlExpiresAt,
    bool SigningUrlExpired,
    DateTime? SignedAt);

/// <summary>
/// Signature panel of a lease (<c>GET /api/leases/{id}/signers</c>, LT-02). <c>ProviderSigningAvailable</c>: the
/// e-signature provider path exists (<c>Features:ESignProvider</c> on and a configured provider); otherwise the contract
/// is signed offline. <c>ContractAvailable</c>: the final contract to sign can be downloaded now; otherwise
/// <c>ContractUnavailableCode</c> says why (<c>contract_template_not_approved</c>, <c>contract_data_missing</c>: only the
/// BOZZA preview exists, LT-03; <c>lease_already_signed</c>).
/// </summary>
public sealed record LeaseSigningStateDto(
    bool ProviderSigningAvailable,
    bool ContractAvailable,
    string? ContractUnavailableCode,
    IReadOnlyList<LeaseSignerDto> Signers);

/// <summary>Result of <c>POST /api/leases/{id}/signing</c> (provider path): the lease status and its signers with their links.</summary>
public sealed record SigningInitiatedDto(Guid LeaseId, LeaseStatus Status, IReadOnlyList<LeaseSignerDto> Signers);

/// <summary>Timeline entry: the event type only, never its payload.</summary>
public sealed record LeaseEventDto(LeaseEventType EventType, DateTime OccurredAt);

/// <summary>
/// Lease detail page. <c>StipulaDate</c>: the day every party had signed; <c>RegistrationDeadline</c>:
/// <c>min(stipula, start) + 30</c> days, null while it is to be determined (LT-04). <c>HasSignedPdf</c>: the contract
/// signed by every party can be downloaded (<c>GET /api/leases/{id}/signed-document</c>, LT-02).
/// </summary>
public sealed record LeaseDetailDto(
    Guid Id,
    Guid PropertyId,
    LeasePropertyDto? Property,
    LeaseStatus Status,
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    DateTime? StipulaDate,
    DateTime? RegistrationDeadline,
    bool HasSignedPdf,
    bool HasExtraEUTenant,
    IReadOnlyList<LeasePartyDto> Parties,
    LeaseRegistrationDto? Registration,
    IReadOnlyList<LeaseEventDto> Events,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Maps lease entities to the API contract.</summary>
public static class LeaseDtoMapper
{
    /// <param name="lease">The lease with its property, parties, registration and events.</param>
    /// <param name="todayInRome">Today on the Rome calendar: the deadline of a lease not signed yet depends on it.</param>
    public static LeaseDetailDto ToDetail(LeaseContract lease, DateTime todayInRome)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return new LeaseDetailDto(
            lease.Id,
            lease.PropertyId,
            ToProperty(lease.Property),
            lease.Status,
            lease.FiscalRegime,
            lease.StartDate,
            lease.EndDate,
            lease.MonthlyRent,
            lease.StipulaDate,
            RliRegistrationDeadline.Resolve(lease, todayInRome),
            // Only a file of the private bucket can be served (LT-02): a provider path left by the old stub is not one.
            HasSignedPdf: StorageKeys.IsValid(lease.SignedPdfStoragePath),
            lease.HasExtraEUTenant,
            lease.Parties.OrderBy(p => p.Role).ThenBy(p => p.LastName, StringComparer.Ordinal).Select(ToParty).ToList(),
            lease.Registration is null ? null : ToRegistration(lease.Registration),
            lease.Events.OrderBy(e => e.OccurredAt).Select(e => new LeaseEventDto(e.EventType, e.OccurredAt)).ToList(),
            lease.CreatedAt,
            lease.UpdatedAt);
    }

    public static LeasePartyDto ToParty(Party party)
    {
        ArgumentNullException.ThrowIfNull(party);

        return new LeasePartyDto(
            party.Id,
            party.Role,
            party.FirstName,
            party.LastName,
            PersonalDataMasking.MaskFiscalCode(party.FiscalCode),
            PersonalDataMasking.MaskEmail(party.ContactEmail),
            party.IsExtraEU);
    }

    public static LeaseRegistrationDto ToRegistration(LeaseRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return new LeaseRegistrationDto(
            registration.Status,
            registration.Channel,
            registration.RegistrationCode,
            registration.RegistrationDate,
            registration.SubmittedAt,
            registration.ConfirmedAt,
            registration.Status == RegistrationStatus.Failed ? registration.FailureCode : null,
            HasReceipt: registration.Status == RegistrationStatus.Registered
                && !string.IsNullOrWhiteSpace(registration.ReceiptStoragePath));
    }

    private static LeasePropertyDto? ToProperty(Property? property) =>
        property is null ? null : new LeasePropertyDto(property.Id, property.Name, property.City);
}
