using Casazen.Core.Enums;

namespace Casazen.Core.DTOs;

public class PublicPropertyDto
{
    public Guid Id { get; set; }
    public string? Slug { get; set; }

    /// <summary>
    /// Current public slug of the org that owns the property: with <see cref="Slug"/> it gives the property page of its
    /// booking site, <c>/book/{orgSlug}/property/{slug}</c> (BK-20, A3-27). Needed by the cross-org public search, where
    /// the caller does not know the org.
    /// </summary>
    public string OrgSlug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// The position for a map, rounded to about a kilometre (<see cref="Entities.PropertyAddress.ToPublicCoordinate"/>); for a property
    /// whose host gave none, the centre of its comune. <c>0, 0</c> only when there is neither: the map then places it by its comune.
    /// </summary>
    public decimal Latitude { get; set; }

    /// <summary>See <see cref="Latitude"/>.</summary>
    public decimal Longitude { get; set; }
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int MaxGuests { get; set; }
    public decimal NightlyRate { get; set; }
    public decimal CleaningFee { get; set; }

    /// <summary>
    /// ISO 4217 code of the property (<see cref="Services.StayCurrency"/>, <c>EUR</c> or <c>CHF</c>): <see cref="NightlyRate"/> and
    /// <see cref="CleaningFee"/> are in it, and so is every quote, charge and refund of a stay (CHF-MONEY).
    /// </summary>
    public string Currency { get; set; } = Services.StayCurrency.Default;
    public IReadOnlyList<string> Amenities { get; set; } = [];
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];
    public string? CinCode { get; set; }
    public CinStatus CinStatus { get; set; }
    public string Timezone { get; set; } = "Europe/Rome";
}
