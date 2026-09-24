using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.DTOs.Leases;

// API contract of the long-term leases (LT-11, A7-17). The API never returns EF entities: a new column on
// LeaseContract, Party or Property must not reach the client by accident. Personal data of the parties is either
// omitted (list) or masked (detail); internal fields (owner id, storage paths, provider ids, event payloads, the
// property's safety checklist) are never part of these types.

/// <summary>The property a lease belongs to, as shown next to the lease.</summary>
public sealed record LeasePropertyDto(Guid Id, string Name, string City);

/// <summary>Row of the lease list: no personal data of the parties, only how many they are.</summary>
public sealed record LeaseSummaryDto(
    Guid Id,
    Guid PropertyId,
    LeasePropertyDto? Property,
    LeaseStatus Status,
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    DateTime RegistrationDeadline,
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

/// <summary>RLI registration state; the provider id and the receipt storage path stay on the server.</summary>
public sealed record LeaseRegistrationDto(
    RegistrationStatus Status,
    string? RegistrationCode,
    DateTime? SubmittedAt,
    DateTime? ConfirmedAt);

/// <summary>Timeline entry: the event type only, never its payload.</summary>
public sealed record LeaseEventDto(LeaseEventType EventType, DateTime OccurredAt);

/// <summary>Lease detail page.</summary>
public sealed record LeaseDetailDto(
    Guid Id,
    Guid PropertyId,
    LeasePropertyDto? Property,
    LeaseStatus Status,
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    DateTime RegistrationDeadline,
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
    public static LeaseDetailDto ToDetail(LeaseContract lease)
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
            lease.RegistrationDeadline,
            HasSignedPdf: !string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath),
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
            registration.RegistrationCode,
            registration.SubmittedAt,
            registration.ConfirmedAt);
    }

    private static LeasePropertyDto? ToProperty(Property? property) =>
        property is null ? null : new LeasePropertyDto(property.Id, property.Name, property.City);
}
