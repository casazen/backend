using Casazen.Core.Enums;

namespace Casazen.Core.DTOs;

public class PropertyDetailResponse
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int MaxGuests { get; set; }
    public decimal NightlyRate { get; set; }
    public decimal CleaningFee { get; set; }
    public decimal DamageDeposit { get; set; }
    public string? CinCode { get; set; }
    public CinStatus CinStatus { get; set; }
    public string Timezone { get; set; } = "Europe/Rome";
    public IReadOnlyList<string> Amenities { get; set; } = [];
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];
    public string HouseRules { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public IReadOnlyList<PropertyDocumentDto> Documents { get; set; } = [];
    public IReadOnlyList<OtaIntegrationSummaryDto> OtaIntegrations { get; set; } = [];
    public BookingsSummaryDto BookingsSummary { get; set; } = new();
    public PricingAdapterSummaryDto PricingAdapterSummary { get; set; } = new();
}

public class PropertyDocumentDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;

    /// <summary>Kind of document (e.g. <c>Ape</c>): the lease form needs it to know whether the APE is on file (A7-06).</summary>
    public DocumentType DocumentType { get; set; }
    public DateTime UploadedAt { get; set; }

    /// <summary>Code printed on the APE (only for <c>Ape</c> documents, LT-10); null until the landlord enters it.</summary>
    public string? ApeCode { get; set; }

    /// <summary>Energy class printed on the APE (only for <c>Ape</c> documents, LT-10).</summary>
    public string? ApeEnergyClass { get; set; }

    /// <summary>
    /// API path of the authenticated download (<c>GET /api/properties/{id}/documents/{docId}/download</c>):
    /// call it with the bearer token; it is not a public link.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;
}

public class OtaIntegrationSummaryDto
{
    public Guid Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool SyncEnabled { get; set; }
    public DateTime LastSyncAt { get; set; }
    public OtaSyncStatus? SyncStatus { get; set; }
}

/// <summary>
/// Bookings KPIs of the property detail (A2-36), on the rules of the host dashboard (<c>StayKpiRules</c>): Europe/Rome
/// calendar dates, never a cancelled booking nor a pending request.
/// </summary>
public class BookingsSummaryDto
{
    /// <summary>Confirmed stays: confirmed, checked in or checked out.</summary>
    public int TotalBookings { get; set; }

    /// <summary>Confirmed check-ins from today on (today's arrivals until the host registers them).</summary>
    public int UpcomingBookings { get; set; }

    /// <summary>Stays in progress today, up to their departure day included.</summary>
    public int ActiveBookings { get; set; }

    /// <summary>Europe/Rome date (midnight UTC) of the next confirmed check-in, today included.</summary>
    public DateTime? NextCheckIn { get; set; }

    /// <summary>Europe/Rome date (midnight UTC) of the next check-out of a confirmed or checked-in stay, today included.</summary>
    public DateTime? NextCheckOut { get; set; }
}

public class PricingAdapterSummaryDto
{
    public bool IsEnabled { get; set; }
    public DateTime? LastAdaptedAt { get; set; }
    public DateTime? NextScheduledRunAt { get; set; }
}
