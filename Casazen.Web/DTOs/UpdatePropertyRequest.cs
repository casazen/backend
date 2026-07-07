using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs;

/// <summary>
/// Request body for updating an existing property. Excludes server-managed fields
/// (<c>Id</c>, <c>OwnerId</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>).
/// Kept as a separate class from <see cref="CreatePropertyRequest"/> for API versioning safety.
/// </summary>
public class UpdatePropertyRequest
{
    /// <summary>Display name of the property.</summary>
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(100, ErrorMessage = "Name cannot exceed 100 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Full description shown to guests.</summary>
    [MaxLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Street address of the property.</summary>
    [Required(ErrorMessage = "Address is required")]
    [MaxLength(500, ErrorMessage = "Address cannot exceed 500 characters")]
    public string Address { get; set; } = string.Empty;

    /// <summary>City where the property is located.</summary>
    [Required(ErrorMessage = "City is required")]
    [MaxLength(50, ErrorMessage = "City cannot exceed 50 characters")]
    public string City { get; set; } = string.Empty;

    /// <summary>Postal / ZIP code.</summary>
    [MaxLength(10, ErrorMessage = "Postal code cannot exceed 10 characters")]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>Geographic latitude of the property.</summary>
    public decimal Latitude { get; set; }

    /// <summary>Geographic longitude of the property.</summary>
    public decimal Longitude { get; set; }

    /// <summary>Number of bedrooms (1–100).</summary>
    [Range(1, 100, ErrorMessage = "Bedrooms must be between 1 and 100")]
    public int Bedrooms { get; set; }

    /// <summary>Number of bathrooms (1–50).</summary>
    [Range(1, 50, ErrorMessage = "Bathrooms must be between 1 and 50")]
    public int Bathrooms { get; set; }

    /// <summary>Maximum number of guests allowed (1–100).</summary>
    [Range(1, 100, ErrorMessage = "Max guests must be between 1 and 100")]
    public int MaxGuests { get; set; }

    /// <summary>Base nightly rate in euros (€0.01–€100,000).</summary>
    [Range(0.01, 100000, ErrorMessage = "Nightly rate must be between €0.01 and €100,000")]
    public decimal NightlyRate { get; set; }

    /// <summary>One-time cleaning fee in euros (€0–€10,000).</summary>
    [Range(0, 10000, ErrorMessage = "Cleaning fee must be between €0 and €10,000")]
    public decimal CleaningFee { get; set; }

    /// <summary>Refundable damage deposit in euros (€0–€50,000).</summary>
    [Range(0, 50000, ErrorMessage = "Damage deposit must be between €0 and €50,000")]
    public decimal DamageDeposit { get; set; }

    /// <summary>List of amenities available at the property.</summary>
    public List<PropertyAmenity> Amenities { get; set; } = new();

    /// <summary>Ordered list of photo URLs for the property listing.</summary>
    public List<string> PhotoUrls { get; set; } = new();

    /// <summary>House rules presented to guests before booking.</summary>
    [MaxLength(1000, ErrorMessage = "House rules cannot exceed 1000 characters")]
    public string HouseRules { get; set; } = string.Empty;

    /// <summary>
    /// Italian Codice Identificativo Nazionale (CIN) — format <c>IT-XXXXX-XXXXXXXXXX</c>.
    /// Required by D.L. 145/2023 for short-term rentals.
    /// </summary>
    [MaxLength(25, ErrorMessage = "CIN code cannot exceed 25 characters")]
    [CinCode]
    public string? CinCode { get; set; }

    /// <summary>
    /// IANA timezone identifier used for booking date calculations
    /// (e.g. <c>Europe/Rome</c>). Defaults to <c>Europe/Rome</c>.
    /// </summary>
    [MaxLength(50, ErrorMessage = "Timezone cannot exceed 50 characters")]
    public string Timezone { get; set; } = "Europe/Rome";

    /// <summary>Optional reference to the cancellation policy applied to new bookings.</summary>
    public Guid? CancellationPolicyId { get; set; }

    /// <summary>Whether the property is visible and bookable.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional URL slug for direct booking links (unique within org).</summary>
    [MaxLength(100)]
    public string? Slug { get; set; }

    /// <summary>
    /// Applies all client-supplied values to an existing <see cref="Property"/> entity in place.
    /// <c>OwnerId</c>, <c>Id</c>, <c>CreatedAt</c> are never touched; only <c>UpdatedAt</c>
    /// is refreshed automatically.
    /// </summary>
    /// <param name="property">The tracked entity to update.</param>
    public void ApplyTo(Property property)
    {
        property.Name = Name;
        property.Description = Description;
        property.Address = Address;
        property.City = City;
        property.PostalCode = PostalCode;
        property.Latitude = Latitude;
        property.Longitude = Longitude;
        property.Bedrooms = Bedrooms;
        property.Bathrooms = Bathrooms;
        property.MaxGuests = MaxGuests;
        property.NightlyRate = NightlyRate;
        property.CleaningFee = CleaningFee;
        property.DamageDeposit = DamageDeposit;
        property.Amenities = Amenities;
        property.PhotoUrls = PhotoUrls;
        property.HouseRules = HouseRules;
        property.CinCode = CinCode;
        property.Timezone = Timezone;
        property.CancellationPolicyId = CancellationPolicyId;
        property.IsActive = IsActive;
        if (Slug is not null)
            property.Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim().ToLowerInvariant();
        property.UpdatedAt = DateTime.UtcNow;
    }
}
