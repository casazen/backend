using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// <para>The nullable fields <see cref="CinCode"/>, <see cref="Slug"/>, <see cref="CancellationPolicyId"/> and
/// <see cref="MinNights"/> (DB-03) are cleared by sending them explicitly as <c>null</c> (or, for the text ones, blank);
/// leaving them out keeps them.</para>
/// <para>Server-managed fields (<c>Id</c>, <c>OwnerId</c>, <c>OrgId</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>, the
/// compliance status) are not part of the request. Validation messages are SharedResources keys.</para>
/// <para>The photo gallery is not part of the request (PC-04, A2-26): photos are uploaded, deleted, ordered and chosen as
/// cover only through <c>/api/properties/{id}/images</c>, which check the file and the storage object. A <c>photoUrls</c>
/// sent in the body is ignored, so it can neither point the public page at an arbitrary URL nor at another property's
/// photo.</para>
/// <para><c>IsActive</c> is not part of the request either (PC-03, A2-05): a generic save could hide a property from
/// its own host's list and detail with no way back from the UI. Pausing is the dedicated, reversible
/// <c>POST /properties/{id}/pause</c> / <c>.../activate</c>; an <c>isActive</c> sent in the body is ignored.</para>
/// Kept as a separate class from <see cref="CreatePropertyRequest"/> for API versioning safety.
/// </remarks>
public class UpdatePropertyRequest
{
    [NotBlankWhenPresent(ErrorMessage = "PropertyNameRequired")]
    [MaxLength(100, ErrorMessage = "PropertyNameTooLong")]
    public string? Name { get; set; }

    [MaxLength(Casazen.Core.Services.ListingTextLimits.DescriptionMaxLength, ErrorMessage = "PropertyDescriptionTooLong")]
    public string? Description { get; set; }

    [NotBlankWhenPresent(ErrorMessage = "PropertyAddressRequired")]
    [MaxLength(500, ErrorMessage = "PropertyAddressTooLong")]
    public string? Address { get; set; }

    [NotBlankWhenPresent(ErrorMessage = "PropertyCityRequired")]
    [MaxLength(50, ErrorMessage = "PropertyCityTooLong")]
    public string? City { get; set; }

    [MaxLength(10, ErrorMessage = "PropertyPostalCodeTooLong")]
    public string? PostalCode { get; set; }

    [RegularExpression("^(IT|CH)$", ErrorMessage = "ValidationError")]
    public string? Country { get; set; }

    /// <summary>
    /// ISTAT code (6 digits) of the comune, chosen from the official list (<c>GET /api/comuni</c>, SU-04); <c>null</c> or blank
    /// clears it (and the region that follows it); leaving it out keeps it. Validated against the list when it changes: 422
    /// <c>comune_istat_unknown</c> / <c>comuni_dataset_unavailable</c>. A <c>city</c> changed <b>without</b> a comune clears the
    /// stored comune: the code no longer says where the property is.
    /// </summary>
    [RegularExpression(ComuneRules.IstatCodePattern, ErrorMessage = "ComuneIstatCodeInvalid")]
    public string? ComuneIstatCode
    {
        get;
        set
        {
            field = value;
            ComuneIstatCodeSent = true;
        }
    }

    /// <summary>
    /// The Swiss municipality of the property (E29/F01/US01/T02, NF-124): its BFS number from the official directory (<c>GET /api/swiss/localities</c>),
    /// a text of one to four digits such as <c>"5192"</c> (Lugano); 400 <c>validation_error</c> otherwise. Only for a property in Switzerland (the
    /// country after a correction of an imported draft in the same request): 422 <c>swiss_municipality_not_applicable</c> otherwise; checked against
    /// the directory when it differs from the stored one (the number a form sends back is not checked again, as for the comune): 422
    /// <c>swiss_municipality_unknown</c> / <c>swiss_places_unavailable</c>. <c>null</c> or empty clears it; leaving it out
    /// keeps it. It is stored only when sent: the place the rules read without it (the one the postal code and the city give, <c>swissPlace</c> of
    /// the record) is never stored by a save. A <c>city</c> or a <c>postalCode</c> changed <b>without</b> it clears the stored municipality, as for the
    /// comune: the number no longer says where the property is; a corrected country clears it too.
    /// </summary>
    [RegularExpression(
        Casazen.Core.Services.SwissPlaceErrorCodes.CodePattern,
        ErrorMessage = Casazen.Core.Services.SwissPlaceErrorCodes.InvalidFormatMessageKey)]
    public string? SwissMunicipalityCode
    {
        get;
        set
        {
            field = value;
            SwissMunicipalityCodeSent = true;
        }
    }

    [JsonIgnore]
    public bool SwissMunicipalityCodeSent { get; private set; }

    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyLatitudeRange")]
    public decimal? Latitude { get; set; }

    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyLongitudeRange")]
    public decimal? Longitude { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyBedroomsRange")]
    public int? Bedrooms { get; set; }

    [Range(1, 50, ErrorMessage = "PropertyBathroomsRange")]
    public int? Bathrooms { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyMaxGuestsRange")]
    public int? MaxGuests { get; set; }

    [Range(typeof(decimal), "0", "100000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyNightlyRateRange")]
    public decimal? NightlyRate { get; set; }

    [Range(PropertyStayRules.MinMinNights, PropertyStayRules.MaxMinNights, ErrorMessage = "PropertyMinNightsRange")]
    public int? MinNights
    {
        get;
        set
        {
            field = value;
            MinNightsSent = true;
        }
    }

    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyWeekendSurchargeRange")]
    public decimal? WeekendSurchargePercent { get; set; }

    /// <summary>
    /// How the property is let (PM-01): <c>Short</c> or <c>Long</c>. Accepted so that a form that sends the whole record
    /// back keeps working, but it must be the <b>stored</b> mode: the mode of an existing property is not changed by this
    /// save, a different value is refused with 422 <c>property_rental_mode_change_not_allowed</c> and
    /// <see cref="ApplyTo"/> never touches it. The change itself is the scheduled mode change of PM-02, which checks the
    /// stays and the leases of the property under its lock.
    /// </summary>
    public RentalMode? RentalMode { get; set; }

    [Range(typeof(decimal), "0", "10000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyCleaningFeeRange")]
    public decimal? CleaningFee { get; set; }

    [Range(typeof(decimal), "0", "50000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyDamageDepositRange")]
    public decimal? DamageDeposit { get; set; }

    public List<PropertyAmenity>? Amenities { get; set; }

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

    [MaxLength(40, ErrorMessage = Casazen.Core.Regulatory.TicinoStructureId.InvalidFormatMessageKey)]
    [TicinoStructureId]
    public string? TicinoStructureId
    {
        get;
        set
        {
            field = value;
            TicinoStructureIdSent = true;
        }
    }

    [JsonIgnore]
    public bool TicinoStructureIdSent { get; private set; }

    [MaxLength(50, ErrorMessage = "PropertyTimezoneInvalid")]
    [IanaTimeZone(ErrorMessage = "PropertyTimezoneInvalid")]
    public string? Timezone { get; set; }

    public Guid? CancellationPolicyId
    {
        get;
        set
        {
            field = value;
            CancellationPolicyIdSent = true;
        }
    }

    [Range(0, 24 * 365, ErrorMessage = "PropertyCancellationHoursRange")]
    public int? CancellationFullRefundHours { get; set; }

    [Range(0, 24 * 365, ErrorMessage = "PropertyCancellationHoursRange")]
    public int? CancellationPartialRefundHours { get; set; }

    [Range(typeof(decimal), "0", "100", ErrorMessage = "PropertyCancellationPercentRange")]
    public decimal? CancellationPartialRefundPercent { get; set; }

    public HostCancellationRefundType? CancellationRefundType { get; set; }

    /// <summary>
    /// Interno / scala (PC-06, A2-19); <c>null</c> or blank removes it. Together with the address it must be unique within
    /// the org: 409 <c>duplicate_property_address</c> otherwise.
    /// </summary>
    [MaxLength(PropertyAddress.UnitMaxLength, ErrorMessage = "PropertyUnitTooLong")]
    public string? Unit
    {
        get;
        set
        {
            field = value;
            UnitSent = true;
        }
    }

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

    public AccommodationType? AccommodationType
    {
        get;
        set
        {
            field = value;
            AccommodationTypeSent = true;
        }
    }

    [Range(PropertyHouseRules.MinBeds, PropertyHouseRules.MaxBeds, ErrorMessage = "PropertyBedsRange")]
    public int? Beds
    {
        get;
        set
        {
            field = value;
            BedsSent = true;
        }
    }

    [Range(PropertyHouseRules.MinAreaSqm, PropertyHouseRules.MaxAreaSqm, ErrorMessage = "PropertyAreaSqmRange")]
    public int? AreaSqm
    {
        get;
        set
        {
            field = value;
            AreaSqmSent = true;
        }
    }

    [MaxLength(PropertyHouseRules.ListingTitleMaxLength, ErrorMessage = "PropertyListingTitleTooLong")]
    public string? ListingTitle
    {
        get;
        set
        {
            field = value;
            ListingTitleSent = true;
        }
    }

    public bool? PublishedOnSite { get; set; }

    public TimeOnly? CheckInFrom
    {
        get;
        set
        {
            field = value;
            CheckInFromSent = true;
        }
    }

    public TimeOnly? CheckOutBy
    {
        get;
        set
        {
            field = value;
            CheckOutBySent = true;
        }
    }

    public SmokingPolicy? SmokingPolicy
    {
        get;
        set
        {
            field = value;
            SmokingPolicySent = true;
        }
    }

    public PetsPolicy? PetsPolicy
    {
        get;
        set
        {
            field = value;
            PetsPolicySent = true;
        }
    }

    public PartiesPolicy? PartiesPolicy
    {
        get;
        set
        {
            field = value;
            PartiesPolicySent = true;
        }
    }

    public ChildrenPolicy? ChildrenPolicy
    {
        get;
        set
        {
            field = value;
            ChildrenPolicySent = true;
        }
    }

    public TimeOnly? QuietHoursFrom
    {
        get;
        set
        {
            field = value;
            QuietHoursFromSent = true;
        }
    }

    public TimeOnly? QuietHoursTo
    {
        get;
        set
        {
            field = value;
            QuietHoursToSent = true;
        }
    }

    [Range(typeof(decimal), "0", "1000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyExtraGuestFeeRange")]
    public decimal? ExtraGuestFeePerNight
    {
        get;
        set
        {
            field = value;
            ExtraGuestFeePerNightSent = true;
        }
    }

    [JsonIgnore]
    public bool AccommodationTypeSent { get; private set; }

    [JsonIgnore]
    public bool BedsSent { get; private set; }

    [JsonIgnore]
    public bool AreaSqmSent { get; private set; }

    [JsonIgnore]
    public bool ListingTitleSent { get; private set; }

    [JsonIgnore]
    public bool CheckInFromSent { get; private set; }

    [JsonIgnore]
    public bool CheckOutBySent { get; private set; }

    [JsonIgnore]
    public bool SmokingPolicySent { get; private set; }

    [JsonIgnore]
    public bool PetsPolicySent { get; private set; }

    [JsonIgnore]
    public bool PartiesPolicySent { get; private set; }

    [JsonIgnore]
    public bool ChildrenPolicySent { get; private set; }

    [JsonIgnore]
    public bool QuietHoursFromSent { get; private set; }

    [JsonIgnore]
    public bool QuietHoursToSent { get; private set; }

    [JsonIgnore]
    public bool ExtraGuestFeePerNightSent { get; private set; }

    [JsonIgnore]
    public bool CinCodeSent { get; private set; }

    [JsonIgnore]
    public bool ComuneIstatCodeSent { get; private set; }

    [JsonIgnore]
    public bool CancellationPolicyIdSent { get; private set; }

    [JsonIgnore]
    public bool UnitSent { get; private set; }

    [JsonIgnore]
    public bool SlugSent { get; private set; }

    [JsonIgnore]
    public bool MinNightsSent { get; private set; }

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
            property.Latitude = PropertyAddress.RoundCoordinate(latitude);
        if (Longitude is { } longitude)
            property.Longitude = PropertyAddress.RoundCoordinate(longitude);
        if (UnitSent)
            property.Unit = PropertyAddress.NormalizeUnit(Unit);
        if (Bedrooms is { } bedrooms)
            property.Bedrooms = bedrooms;
        if (Bathrooms is { } bathrooms)
            property.Bathrooms = bathrooms;
        if (MaxGuests is { } maxGuests)
            property.MaxGuests = maxGuests;
        if (NightlyRate is { } nightlyRate)
            property.NightlyRate = nightlyRate;
        if (MinNightsSent)
            property.MinNights = MinNights;
        if (WeekendSurchargePercent is { } weekendSurcharge)
            property.WeekendSurchargePercent = weekendSurcharge;
        if (CleaningFee is { } cleaningFee)
            property.CleaningFee = cleaningFee;
        if (DamageDeposit is { } damageDeposit)
            property.DamageDeposit = damageDeposit;
        if (Amenities is not null)
            property.Amenities = Amenities;
        if (HouseRules is not null)
            property.HouseRules = HouseRules;
        if (Timezone is not null)
            property.Timezone = Timezone.Trim();
        if (CinCodeSent)
            property.CinCode = CinCode;
        if (TicinoStructureIdSent)
            property.TicinoStructureId = Casazen.Core.Regulatory.TicinoStructureId.Normalize(TicinoStructureId);
        if (CancellationPolicyIdSent)
            property.CancellationPolicyId = CancellationPolicyId;
        if (CancellationFullRefundHours is { } fullHours)
            property.CancellationFullRefundHours = fullHours;
        if (CancellationPartialRefundHours is { } partialHours)
            property.CancellationPartialRefundHours = partialHours;
        if (CancellationPartialRefundPercent is { } partialPercent)
            property.CancellationPartialRefundPercent = partialPercent;
        if (CancellationRefundType is { } refundType)
            property.CancellationRefundType = refundType;
        if (SlugSent)
            property.Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim().ToLowerInvariant();

        if (AccommodationTypeSent)
            property.AccommodationType = AccommodationType;
        if (BedsSent)
            property.Beds = Beds;
        if (AreaSqmSent)
            property.AreaSqm = AreaSqm;
        if (ListingTitleSent)
            property.ListingTitle = ListingTitle;
        if (PublishedOnSite is { } publishedOnSite)
            property.PublishedOnSite = publishedOnSite;
        if (CheckInFromSent)
            property.CheckInFrom = CheckInFrom;
        if (CheckOutBySent)
            property.CheckOutBy = CheckOutBy;
        if (SmokingPolicySent)
            property.SmokingPolicy = SmokingPolicy;
        if (PetsPolicySent)
            property.PetsPolicy = PetsPolicy;
        if (PartiesPolicySent)
            property.PartiesPolicy = PartiesPolicy;
        if (ChildrenPolicySent)
            property.ChildrenPolicy = ChildrenPolicy;
        if (QuietHoursFromSent)
            property.QuietHoursFrom = QuietHoursFrom;
        if (QuietHoursToSent)
            property.QuietHoursTo = QuietHoursTo;
        if (ExtraGuestFeePerNightSent)
            property.ExtraGuestFeePerNight = ExtraGuestFeePerNight;

        property.UpdatedAt = DateTime.UtcNow;
    }
}
