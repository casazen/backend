using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs;

/// <summary>
/// Request body for creating a new property. Excludes server-managed fields
/// (<c>Id</c>, <c>OwnerId</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>). A new property is always active and not paused
/// (PC-03, A2-05): <c>isActive</c> is not accepted, pausing is the dedicated <c>POST /properties/{id}/pause</c>.
/// A new property has an empty photo gallery (PC-04, A2-26): photos are uploaded afterwards with
/// <c>POST /api/properties/{id}/images</c>; a <c>photoUrls</c> sent in the body is ignored.
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

    /// <summary>
    /// Interno / scala of the apartment in the building (e.g. <c>int. 5</c>, <c>Scala B int. 5</c>), optional (PC-06,
    /// A2-19). Several apartments of the same building are different properties of the org if their units differ; the
    /// same address and unit twice in an org is refused with 409 <c>duplicate_property_address</c>.
    /// </summary>
    [MaxLength(PropertyAddress.UnitMaxLength, ErrorMessage = "PropertyUnitTooLong")]
    public string? Unit { get; set; }

    /// <summary>City where the property is located.</summary>
    [Required(ErrorMessage = "PropertyCityRequired")]
    [MaxLength(50, ErrorMessage = "PropertyCityTooLong")]
    public string City { get; set; } = string.Empty;

    /// <summary>Postal / ZIP code.</summary>
    [MaxLength(10, ErrorMessage = "PropertyPostalCodeTooLong")]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// Geographic latitude in degrees, -90 to 90 (A2-33); more than 6 decimals (about 0.1 m) are rounded. <c>0</c> with a
    /// longitude of <c>0</c> = not set.
    /// </summary>
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyLatitudeRange")]
    public decimal Latitude { get; set; }

    /// <summary>Geographic longitude in degrees, -180 to 180 (A2-33); more than 6 decimals are rounded.</summary>
    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyLongitudeRange")]
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
        Unit = PropertyAddress.NormalizeUnit(Unit),
        City = City,
        PostalCode = PostalCode,
        Latitude = PropertyAddress.RoundCoordinate(Latitude),
        Longitude = PropertyAddress.RoundCoordinate(Longitude),
        Bedrooms = Bedrooms,
        Bathrooms = Bathrooms,
        MaxGuests = MaxGuests,
        NightlyRate = NightlyRate,
        CleaningFee = CleaningFee,
        DamageDeposit = DamageDeposit,
        Amenities = Amenities,
        HouseRules = HouseRules,
        CinCode = CinCode,
        Timezone = Timezone,
        CancellationPolicyId = CancellationPolicyId,
        Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim().ToLowerInvariant(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
