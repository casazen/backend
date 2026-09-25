using Casazen.Core.Entities;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Guest row of <c>GET /api/guests</c> (TN-1): only what the list shows, never documents or consent data.
/// </summary>
public sealed class GuestSummaryDto
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Guest detail of <c>/api/guests/{id}</c> (TN-1). Replaces the entity: no navigation properties
/// (bookings, Alloggiati reports), no tenant key, no storage path of the document scan and no consent IP.
/// The document number is masked like everywhere else in the app (CO-14): the full number only comes from the audited
/// <c>GET /api/guests/{id}/document-number</c>.
/// </summary>
public sealed class GuestDto
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;

    public DateTime? DateOfBirth { get; init; }
    public string PlaceOfBirth { get; init; } = string.Empty;
    public string Nationality { get; init; } = string.Empty;
    public Gender? Gender { get; init; }
    public GuestDocumentType? DocumentType { get; init; }
    /// <summary>Document number masked as <c>*****</c> plus the last 3 characters; null when there is none.</summary>
    public string? DocumentNumberMasked { get; init; }
    public DateTime? DocumentIssueDate { get; init; }
    public DateTime? DocumentExpiryDate { get; init; }
    public string DocumentIssuingCountry { get; init; } = string.Empty;
    public bool HasDocumentScan { get; init; }

    public string Notes { get; init; } = string.Empty;

    public DateTime? DataProcessingConsentDate { get; init; }
    public DateTime? ConsentDate { get; init; }
    public string ConsentVersion { get; init; } = string.Empty;
    public bool MarketingConsent { get; init; }
    public DateTime? MarketingConsentDate { get; init; }
    public DateTime? DataRetentionExpiryDate { get; init; }
    public string DataProcessingPurpose { get; init; } = string.Empty;
    public bool ErasureRequested { get; init; }
    public DateTime? ErasureRequestedDate { get; init; }
    public DateTime? DataAnonymizedDate { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime? DeletedAt { get; init; }
    public string DeletionReason { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public static class GuestDtoMapper
{
    public static GuestSummaryDto ToSummary(Guest guest) => new()
    {
        Id = guest.Id,
        FirstName = guest.FirstName,
        LastName = guest.LastName,
        Email = guest.Email,
        PhoneNumber = guest.PhoneNumber,
        City = guest.City,
        Country = guest.Country,
        CreatedAt = guest.CreatedAt,
    };

    public static GuestDto ToDto(Guest guest) => new()
    {
        Id = guest.Id,
        FirstName = guest.FirstName,
        LastName = guest.LastName,
        Email = guest.Email,
        PhoneNumber = guest.PhoneNumber,
        Address = guest.Address,
        City = guest.City,
        PostalCode = guest.PostalCode,
        Country = guest.Country,
        DateOfBirth = guest.DateOfBirth,
        PlaceOfBirth = guest.PlaceOfBirth,
        Nationality = guest.Nationality,
        Gender = guest.Gender,
        DocumentType = guest.DocumentType,
        DocumentNumberMasked = GuestCheckInService.MaskDocumentNumber(guest.DocumentNumber),
        DocumentIssueDate = guest.DocumentIssueDate,
        DocumentExpiryDate = guest.DocumentExpiryDate,
        DocumentIssuingCountry = guest.DocumentIssuingCountry,
        HasDocumentScan = !string.IsNullOrWhiteSpace(guest.DocumentScanUrl),
        Notes = guest.Notes,
        DataProcessingConsentDate = guest.DataProcessingConsentDate,
        ConsentDate = guest.ConsentDate,
        ConsentVersion = guest.ConsentVersion,
        MarketingConsent = guest.MarketingConsent,
        MarketingConsentDate = guest.MarketingConsentDate,
        DataRetentionExpiryDate = guest.DataRetentionExpiryDate,
        DataProcessingPurpose = guest.DataProcessingPurpose,
        ErasureRequested = guest.ErasureRequested,
        ErasureRequestedDate = guest.ErasureRequestedDate,
        DataAnonymizedDate = guest.DataAnonymizedDate,
        IsDeleted = guest.IsDeleted,
        DeletedAt = guest.DeletedAt,
        DeletionReason = guest.DeletionReason,
        CreatedAt = guest.CreatedAt,
        UpdatedAt = guest.UpdatedAt,
    };
}

/// <summary>
/// Full document number of a guest, from the explicit and audited <c>GET /api/guests/{id}/document-number</c> (CO-14):
/// every other guest answer carries it masked.
/// </summary>
public sealed class GuestDocumentNumberDto
{
    public Guid GuestId { get; init; }
    public string DocumentNumber { get; init; } = string.Empty;
}
