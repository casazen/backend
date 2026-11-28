using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;

namespace Casazen.Core.DTOs;

public class PropertyDetailResponse
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;

    public string? Unit { get; set; }
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int MaxGuests { get; set; }
    public decimal NightlyRate { get; set; }

    public int? MinNights { get; set; }

    public decimal WeekendSurchargePercent { get; set; }
    public decimal CleaningFee { get; set; }
    public decimal DamageDeposit { get; set; }
    public string? CinCode { get; set; }
    public CinStatus CinStatus { get; set; }

    /// <summary>
    /// True when the CIN is valid, the property has a comune chosen from the official list and the ISTAT code inside the CIN
    /// is another one (SU-04): a non-blocking warning, the CIN does not change after a relocation or reclassification.
    /// </summary>
    public bool CinIstatMismatch { get; set; }

    /// <summary>ISTAT code of the comune chosen from the official list; null until chosen.</summary>
    public string? ComuneIstatCode { get; set; }

    public string? RegionCode { get; set; }
    public string Timezone { get; set; } = "Europe/Rome";
    public IReadOnlyList<string> Amenities { get; set; } = [];
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];
    public string HouseRules { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    public bool IsPaused { get; set; }

    public DateTime? PausedAt { get; set; }

    public RentalMode RentalMode { get; set; }
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

    public DocumentType DocumentType { get; set; }
    public DateTime UploadedAt { get; set; }

    public string? ApeCode { get; set; }

    public string? ApeEnergyClass { get; set; }

    public string DownloadUrl { get; set; } = string.Empty;

    public string? ApeCheck { get; set; }

    public IReadOnlyList<string>? ApeUnreadFields { get; set; }

    /// <summary>
    /// Only with <see cref="ApeCheck"/>: where <see cref="ApeCode"/> came from when it was found automatically, <c>Document</c> (read
    /// on the file) or <c>Registry</c> (the public register of the region, the file did not show it legibly); null otherwise.
    /// </summary>
    public string? ApeIdentificationSource { get; set; }
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

public class BookingsSummaryDto
{
    public int TotalBookings { get; set; }

    public int UpcomingBookings { get; set; }

    public int ActiveBookings { get; set; }

    public DateTime? NextCheckIn { get; set; }

    public DateTime? NextCheckOut { get; set; }
}

public class PricingAdapterSummaryDto
{
    public bool IsEnabled { get; set; }
    public DateTime? LastAdaptedAt { get; set; }

    public DateOnly? NextRunOn { get; set; }
}
