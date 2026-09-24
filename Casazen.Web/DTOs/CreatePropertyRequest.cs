using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs;

/// <summary>
/// Request body for creating a new property. Excludes server-managed fields
/// (<c>Id</c>, <c>OwnerId</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>).
/// </summary>
public class CreatePropertyRequest
{
    /// <summary>Display name of the property.</summary>
    [Required(ErrorMessage = "PropertyNameRequired")]
    [MaxLength(100, ErrorMessage = "PropertyNameTooLong")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Full description shown to guests.</summary>
    [MaxLength(2000, ErrorMessage = "PropertyDescriptionTooLong")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Street address of the property.</summary>
    [Required(ErrorMessage = "PropertyAddressRequired")]
    [MaxLength(500, ErrorMessage = "PropertyAddressTooLong")]
    public string Address { get; set; } = string.Empty;

    /// <summary>City where the property is located.</summary>
    [Required(ErrorMessage = "PropertyCityRequired")]
    [MaxLength(50, ErrorMessage = "PropertyCityTooLong")]
    public string City { get; set; } = string.Empty;

    /// <summary>Postal / ZIP code.</summary>
    [MaxLength(10, ErrorMessage = "PropertyPostalCodeTooLong")]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>Geographic latitude of the property.</summary>
    public decimal Latitude { get; set; }

    /// <summary>Geographic longitude of the property.</summary>
    public decimal Longitude { get; set; }

    /// <summary>Number of bedrooms (0–100): <c>0</c> is a studio flat (monolocale, A2-27).</summary>
    [Range(0, 100, ErrorMessage = "PropertyBedroomsRange")]
    public int Bedrooms { get; set; }

    /// <summary>Number of bathrooms, whole number (1–50, A2-27).</summary>
    [Range(1, 50, ErrorMessage = "PropertyBathroomsRange")]
    public int Bathrooms { get; set; }

    /// <summary>
    /// Maximum number of guests of a short stay (0–100). <c>0</c> = not set: a property kept only for long-term leases
    /// has none (A7-06); the short-stay listing cannot be activated until it is set (activation wizard).
    /// </summary>
    [Range(0, 100, ErrorMessage = "PropertyMaxGuestsRange")]
    public int MaxGuests { get; set; }

    /// <summary>
    /// Base nightly rate in euros (€0–€100,000). <c>0</c> = no short-stay rate (long-term only property, A7-06); the
    /// short-stay listing cannot be activated until it is set (activation wizard).
    /// </summary>
    [Range(0, 100000, ErrorMessage = "PropertyNightlyRateRange")]
    public decimal NightlyRate { get; set; }

    /// <summary>One-time cleaning fee in euros (€0–€10,000).</summary>
    [Range(0, 10000, ErrorMessage = "PropertyCleaningFeeRange")]
    public decimal CleaningFee { get; set; }

    /// <summary>Refundable damage deposit in euros (€0–€50,000).</summary>
    [Range(0, 50000, ErrorMessage = "PropertyDamageDepositRange")]
    public decimal DamageDeposit { get; set; }

    /// <summary>List of amenities available at the property.</summary>
    public List<PropertyAmenity> Amenities { get; set; } = new();

    /// <summary>Ordered list of photo URLs for the property listing.</summary>
    public List<string> PhotoUrls { get; set; } = new();

    /// <summary>House rules presented to guests before booking.</summary>
    [MaxLength(1000, ErrorMessage = "PropertyHouseRulesTooLong")]
    public string HouseRules { get; set; } = string.Empty;

    /// <summary>
    /// Italian Codice Identificativo Nazionale (CIN), e.g. <c>IT058091C27G5FFZDZ</c>. Spaces and hyphens are
    /// ignored and the value is stored normalized (see <c>CinFormat</c>).
    /// Required by D.L. 145/2023 for short-term rentals.
    /// </summary>
    // Raw input may contain spaces or hyphens; the stored (normalized) CIN is at most 18 characters.
    [MaxLength(40, ErrorMessage = CinFormat.InvalidFormatMessageKey)]
    [CinCode]
    public string? CinCode { get; set; }

    /// <summary>
    /// IANA timezone identifier used for booking date calculations
    /// (e.g. <c>Europe/Rome</c>). Defaults to <c>Europe/Rome</c>.
    /// </summary>
    [MaxLength(50, ErrorMessage = "PropertyTimezoneInvalid")]
    [IanaTimeZone(ErrorMessage = "PropertyTimezoneInvalid")]
    public string Timezone { get; set; } = "Europe/Rome";

    /// <summary>Optional reference to the cancellation policy applied to new bookings.</summary>
    public Guid? CancellationPolicyId { get; set; }

    /// <summary>Whether the property is visible and bookable. Defaults to <c>true</c>.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional URL slug for direct booking links (unique within org).</summary>
    [MaxLength(100, ErrorMessage = "PropertySlugTooLong")]
    public string? Slug { get; set; }

    /// <summary>
    /// Maps the DTO to a new <see cref="Property"/> entity.
    /// <paramref name="ownerId"/> is taken from the caller's JWT claim and
    /// must not be supplied by the client.
    /// </summary>
    /// <param name="ownerId">Auth0 subject claim of the authenticated user.</param>
    public Property ToProperty(string ownerId) => new()
    {
        OwnerId = ownerId,
        Name = Name,
        Description = Description,
        Address = Address,
        City = City,
        PostalCode = PostalCode,
        Latitude = Latitude,
        Longitude = Longitude,
        Bedrooms = Bedrooms,
        Bathrooms = Bathrooms,
        MaxGuests = MaxGuests,
        NightlyRate = NightlyRate,
        CleaningFee = CleaningFee,
        DamageDeposit = DamageDeposit,
        Amenities = Amenities,
        PhotoUrls = PhotoUrls,
        HouseRules = HouseRules,
        CinCode = CinCode,
        Timezone = Timezone,
        CancellationPolicyId = CancellationPolicyId,
        IsActive = IsActive,
        Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim().ToLowerInvariant(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
