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
    public DateTime UploadedAt { get; set; }
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
    public DateTime? NextScheduledRunAt { get; set; }
}
