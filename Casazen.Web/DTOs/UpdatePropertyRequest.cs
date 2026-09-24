using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs;

/// <summary>
/// Request body of <c>PUT /api/properties/{id}</c>, with <b>PATCH semantics</b> (A2-04): a field that is not in the
/// body (or is <c>null</c>) keeps its stored value, so a client that does not know or does not show a field (cleaning
/// fee, damage deposit, house rules, timezone, cancellation policy...) never resets it. A field that is sent is
/// validated and applied.
/// </summary>
/// <remarks>
/// <para>The nullable fields <see cref="CinCode"/>, <see cref="Slug"/> and <see cref="CancellationPolicyId"/> are
/// cleared by sending them explicitly as <c>null</c> (or, for the text ones, blank); leaving them out keeps them.</para>
/// <para>Server-managed fields (<c>Id</c>, <c>OwnerId</c>, <c>OrgId</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>, the
/// compliance status) are not part of the request. Validation messages are SharedResources keys.</para>
/// Kept as a separate class from <see cref="CreatePropertyRequest"/> for API versioning safety.
/// </remarks>
public class UpdatePropertyRequest
{
    /// <summary>Display name of the property; when sent, not blank.</summary>
    [NotBlankWhenPresent(ErrorMessage = "PropertyNameRequired")]
    [MaxLength(100, ErrorMessage = "PropertyNameTooLong")]
    public string? Name { get; set; }

    /// <summary>Full description shown to guests (may be sent empty).</summary>
    [MaxLength(2000, ErrorMessage = "PropertyDescriptionTooLong")]
    public string? Description { get; set; }

    /// <summary>Street address of the property; when sent, not blank.</summary>
    [NotBlankWhenPresent(ErrorMessage = "PropertyAddressRequired")]
    [MaxLength(500, ErrorMessage = "PropertyAddressTooLong")]
    public string? Address { get; set; }

    /// <summary>City where the property is located; when sent, not blank.</summary>
    [NotBlankWhenPresent(ErrorMessage = "PropertyCityRequired")]
    [MaxLength(50, ErrorMessage = "PropertyCityTooLong")]
    public string? City { get; set; }

    /// <summary>Postal / ZIP code.</summary>
    [MaxLength(10, ErrorMessage = "PropertyPostalCodeTooLong")]
    public string? PostalCode { get; set; }

    /// <summary>Geographic latitude of the property.</summary>
    public decimal? Latitude { get; set; }

    /// <summary>Geographic longitude of the property.</summary>
    public decimal? Longitude { get; set; }

    /// <summary>Number of bedrooms (0–100): <c>0</c> is a studio flat (monolocale, A2-27).</summary>
    [Range(0, 100, ErrorMessage = "PropertyBedroomsRange")]
    public int? Bedrooms { get; set; }

    /// <summary>Number of bathrooms, whole number (1–50, A2-27).</summary>
    [Range(1, 50, ErrorMessage = "PropertyBathroomsRange")]
    public int? Bathrooms { get; set; }

    /// <summary>
    /// Maximum number of guests of a short stay (0–100). <c>0</c> = not set: a property kept only for long-term leases
    /// has none (A7-06); the short-stay listing cannot be activated until it is set (activation wizard).
    /// </summary>
    [Range(0, 100, ErrorMessage = "PropertyMaxGuestsRange")]
    public int? MaxGuests { get; set; }

    /// <summary>
    /// Base nightly rate in euros (€0–€100,000). <c>0</c> = no short-stay rate (long-term only property, A7-06); the
    /// short-stay listing cannot be activated until it is set (activation wizard).
    /// </summary>
    [Range(typeof(decimal), "0", "100000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyNightlyRateRange")]
    public decimal? NightlyRate { get; set; }

    /// <summary>One-time cleaning fee in euros (€0–€10,000), charged by the checkout.</summary>
    [Range(typeof(decimal), "0", "10000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyCleaningFeeRange")]
    public decimal? CleaningFee { get; set; }

    /// <summary>Refundable damage deposit in euros (€0–€50,000).</summary>
    [Range(typeof(decimal), "0", "50000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyDamageDepositRange")]
    public decimal? DamageDeposit { get; set; }

    /// <summary>List of amenities available at the property (the whole list replaces the stored one).</summary>
    public List<PropertyAmenity>? Amenities { get; set; }

    /// <summary>Ordered list of photo URLs (the whole list replaces the stored one; the web form does not send it).</summary>
    public List<string>? PhotoUrls { get; set; }

    /// <summary>House rules presented to guests before booking (may be sent empty).</summary>
    [MaxLength(1000, ErrorMessage = "PropertyHouseRulesTooLong")]
    public string? HouseRules { get; set; }

    /// <summary>
    /// Italian Codice Identificativo Nazionale (CIN), e.g. <c>IT058091C27G5FFZDZ</c>. Spaces and hyphens are
    /// ignored and the value is stored normalized (see <c>CinFormat</c>); <c>null</c> or blank clears it.
    /// Required by D.L. 145/2023 for short-term rentals.
    /// </summary>
    // Raw input may contain spaces or hyphens; the stored (normalized) CIN is at most 18 characters.
    [MaxLength(40, ErrorMessage = CinFormat.InvalidFormatMessageKey)]
    [CinCode]
    public string? CinCode
    {
        get;
        set
        {
            field = value;
            CinCodeSent = true;
        }
    }

    /// <summary>
    /// IANA timezone identifier used for booking date calculations (e.g. <c>Europe/Rome</c>); unknown and Windows ids
    /// are rejected.
    /// </summary>
    [MaxLength(50, ErrorMessage = "PropertyTimezoneInvalid")]
    [IanaTimeZone(ErrorMessage = "PropertyTimezoneInvalid")]
    public string? Timezone { get; set; }

    /// <summary>
    /// Cancellation policy applied to new bookings (one of <c>GET /api/properties/cancellation-policies</c>);
    /// <c>null</c> removes it. An id that does not exist is refused with 422 <c>cancellation_policy_not_found</c>.
    /// </summary>
    public Guid? CancellationPolicyId
    {
        get;
        set
        {
            field = value;
            CancellationPolicyIdSent = true;
        }
    }

    /// <summary>Whether the property is visible and bookable.</summary>
    public bool? IsActive { get; set; }

    /// <summary>URL slug for direct booking links (unique within org); <c>null</c> or blank removes it.</summary>
    [MaxLength(100, ErrorMessage = "PropertySlugTooLong")]
    public string? Slug
    {
        get;
        set
        {
            field = value;
            SlugSent = true;
        }
    }

    /// <summary>True when the body carries <see cref="CinCode"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool CinCodeSent { get; private set; }

    /// <summary>True when the body carries <see cref="CancellationPolicyId"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool CancellationPolicyIdSent { get; private set; }

    /// <summary>True when the body carries <see cref="Slug"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool SlugSent { get; private set; }

    /// <summary>
    /// Applies the fields present in the request to a tracked <see cref="Property"/> in place and leaves the others
    /// untouched. <c>Id</c>, <c>OwnerId</c>, <c>OrgId</c> and <c>CreatedAt</c> are never touched; <c>UpdatedAt</c> is
    /// refreshed. The CIN is normalized and the slug checked by <c>PropertyService.UpdatePropertyAsync</c>.
    /// </summary>
    /// <param name="property">The tracked entity to update.</param>
    public void ApplyTo(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (Name is not null)
            property.Name = Name;
        if (Description is not null)
            property.Description = Description;
        if (Address is not null)
            property.Address = Address;
        if (City is not null)
            property.City = City;
        if (PostalCode is not null)
            property.PostalCode = PostalCode;
        if (Latitude is { } latitude)
            property.Latitude = latitude;
        if (Longitude is { } longitude)
            property.Longitude = longitude;
        if (Bedrooms is { } bedrooms)
            property.Bedrooms = bedrooms;
        if (Bathrooms is { } bathrooms)
            property.Bathrooms = bathrooms;
        if (MaxGuests is { } maxGuests)
            property.MaxGuests = maxGuests;
        if (NightlyRate is { } nightlyRate)
            property.NightlyRate = nightlyRate;
        if (CleaningFee is { } cleaningFee)
            property.CleaningFee = cleaningFee;
        if (DamageDeposit is { } damageDeposit)
            property.DamageDeposit = damageDeposit;
        if (Amenities is not null)
            property.Amenities = Amenities;
        if (PhotoUrls is not null)
            property.PhotoUrls = PhotoUrls;
        if (HouseRules is not null)
            property.HouseRules = HouseRules;
        if (Timezone is not null)
            property.Timezone = Timezone.Trim();
        if (IsActive is { } isActive)
            property.IsActive = isActive;
        if (CinCodeSent)
            property.CinCode = CinCode;
        if (CancellationPolicyIdSent)
            property.CancellationPolicyId = CancellationPolicyId;
        if (SlugSent)
            property.Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim().ToLowerInvariant();

        property.UpdatedAt = DateTime.UtcNow;
    }
}
