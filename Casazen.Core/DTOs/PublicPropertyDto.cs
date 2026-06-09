using Casazen.Core.Enums;

namespace Casazen.Core.DTOs;

public class PublicPropertyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int MaxGuests { get; set; }
    public decimal NightlyRate { get; set; }
    public decimal CleaningFee { get; set; }
    public IReadOnlyList<string> Amenities { get; set; } = [];
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];
    public string? CinCode { get; set; }
    public CinStatus CinStatus { get; set; }
    public string Timezone { get; set; } = "Europe/Rome";
}
