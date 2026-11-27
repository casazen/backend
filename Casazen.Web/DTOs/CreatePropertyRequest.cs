using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
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
    [Required(ErrorMessage = "PropertyNameRequired")]
    [MaxLength(100, ErrorMessage = "PropertyNameTooLong")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(Casazen.Core.Services.ListingTextLimits.DescriptionMaxLength, ErrorMessage = "PropertyDescriptionTooLong")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "PropertyAddressRequired")]
    [MaxLength(500, ErrorMessage = "PropertyAddressTooLong")]
    public string Address { get; set; } = string.Empty;

    [MaxLength(PropertyAddress.UnitMaxLength, ErrorMessage = "PropertyUnitTooLong")]
    public string? Unit { get; set; }

    [Required(ErrorMessage = "PropertyCityRequired")]
    [MaxLength(50, ErrorMessage = "PropertyCityTooLong")]
    public string City { get; set; } = string.Empty;

    [MaxLength(10, ErrorMessage = "PropertyPostalCodeTooLong")]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// ISTAT code (6 digits) of the comune, chosen from the official list (<c>GET /api/comuni</c>, SU-04). Optional: without it
    /// the property has no trusted comune (no regional rules, no CIN check, the tourist tax by the written city). Validated
    /// against the list: 422 <c>comune_istat_unknown</c> / <c>comuni_dataset_unavailable</c>. The region follows it.
    /// </summary>
    [RegularExpression(ComuneRules.IstatCodePattern, ErrorMessage = "ComuneIstatCodeInvalid")]
    public string? ComuneIstatCode { get; set; }

    /// <summary>
    /// The Swiss municipality of the property (E29/F01/US01/T02, NF-124): its BFS number from the official directory (<c>GET /api/swiss/localities</c>),
    /// a text of one to four digits such as <c>"5192"</c> (Lugano); 400 <c>validation_error</c> otherwise. Only for a property in Switzerland: this
    /// request has no country, so the property it creates is Italian (a Swiss one comes from the import of a channel manager) and a value is refused
    /// with 422 <c>swiss_municipality_not_applicable</c>. Left out, <c>null</c> or empty = none.
    /// </summary>
    [RegularExpression(SwissPlaceErrorCodes.CodePattern, ErrorMessage = SwissPlaceErrorCodes.InvalidFormatMessageKey)]
    public string? SwissMunicipalityCode { get; set; }

    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyLatitudeRange")]
    public decimal Latitude { get; set; }

    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyLongitudeRange")]
    public decimal Longitude { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyBedroomsRange")]
    public int Bedrooms { get; set; }

    [Range(1, 50, ErrorMessage = "PropertyBathroomsRange")]
    public int Bathrooms { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyMaxGuestsRange")]
    public int MaxGuests { get; set; }

    [Range(0, 100000, ErrorMessage = "PropertyNightlyRateRange")]
    public decimal NightlyRate { get; set; }

    [Range(PropertyStayRules.MinMinNights, PropertyStayRules.MaxMinNights, ErrorMessage = "PropertyMinNightsRange")]
    public int? MinNights { get; set; }

    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyWeekendSurchargeRange")]
    public decimal WeekendSurchargePercent { get; set; }

    /// <summary>
    /// How the property is let (PM-01, D19): <c>Short</c> (short stays) or <c>Long</c> (long-term leases), exclusive.
    /// Optional. <b>Compatibility rule</b> (<see cref="PropertyRentalModeRules.ResolveForCreation"/>): without it, a request
    /// with no guests (<c>maxGuests: 0</c>) <b>and</b> no rate (<c>nightlyRate: 0</c>) — what the long-rent form has always
    /// sent (A7-06) — creates a <c>Long</c> property; any other request creates a <c>Short</c> one. A mode that is sent
    /// is always used as it is.
    /// </summary>
    public RentalMode? RentalMode { get; set; }

    [Range(0, 10000, ErrorMessage = "PropertyCleaningFeeRange")]
    public decimal CleaningFee { get; set; }

    [Range(0, 50000, ErrorMessage = "PropertyDamageDepositRange")]
    public decimal DamageDeposit { get; set; }

    public List<PropertyAmenity> Amenities { get; set; } = new();

    [MaxLength(1000, ErrorMessage = "PropertyHouseRulesTooLong")]
    public string HouseRules { get; set; } = string.Empty;

    public AccommodationType? AccommodationType { get; set; }

    [Range(PropertyHouseRules.MinBeds, PropertyHouseRules.MaxBeds, ErrorMessage = "PropertyBedsRange")]
    public int? Beds { get; set; }

    [Range(PropertyHouseRules.MinAreaSqm, PropertyHouseRules.MaxAreaSqm, ErrorMessage = "PropertyAreaSqmRange")]
    public int? AreaSqm { get; set; }

    [MaxLength(PropertyHouseRules.ListingTitleMaxLength, ErrorMessage = "PropertyListingTitleTooLong")]
    public string? ListingTitle { get; set; }

    public bool? PublishedOnSite { get; set; }

    public TimeOnly? CheckInFrom { get; set; }

    public TimeOnly? CheckOutBy { get; set; }

    public SmokingPolicy? SmokingPolicy { get; set; }

    public PetsPolicy? PetsPolicy { get; set; }

    public PartiesPolicy? PartiesPolicy { get; set; }

    public ChildrenPolicy? ChildrenPolicy { get; set; }

    public TimeOnly? QuietHoursFrom { get; set; }

    public TimeOnly? QuietHoursTo { get; set; }

    [Range(typeof(decimal), "0", "1000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyExtraGuestFeeRange")]
    public decimal? ExtraGuestFeePerNight { get; set; }

    /// <summary>
    /// Italian Codice Identificativo Nazionale (CIN), e.g. <c>IT058091C27G5FFZDZ</c>. Spaces and hyphens are
    /// ignored and the value is stored normalized (see <c>CinFormat</c>).
    /// Required by D.L. 145/2023 for short-term rentals.
    /// </summary>
    // Raw input may contain spaces or hyphens; the stored (normalized) CIN is at most 18 characters.
    [MaxLength(40, ErrorMessage = CinFormat.InvalidFormatMessageKey)]
    [CinCode]
    public string? CinCode { get; set; }

    [MaxLength(40, ErrorMessage = Casazen.Core.Regulatory.TicinoStructureId.InvalidFormatMessageKey)]
    [TicinoStructureId]
    public string? TicinoStructureId { get; set; }

    [MaxLength(50, ErrorMessage = "PropertyTimezoneInvalid")]
    [IanaTimeZone(ErrorMessage = "PropertyTimezoneInvalid")]
    public string Timezone { get; set; } = "Europe/Rome";

    public Guid? CancellationPolicyId { get; set; }

    [Range(0, 24 * 365, ErrorMessage = "PropertyCancellationHoursRange")]
    public int? CancellationFullRefundHours { get; set; }

    [Range(0, 24 * 365, ErrorMessage = "PropertyCancellationHoursRange")]
    public int? CancellationPartialRefundHours { get; set; }

    [Range(typeof(decimal), "0", "100", ErrorMessage = "PropertyCancellationPercentRange")]
    public decimal? CancellationPartialRefundPercent { get; set; }

    public HostCancellationRefundType CancellationRefundType { get; set; } = HostCancellationRefundType.Percent;

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
        MinNights = MinNights,
        WeekendSurchargePercent = WeekendSurchargePercent,
        RentalMode = PropertyRentalModeRules.ResolveForCreation(RentalMode, MaxGuests, NightlyRate),
        CleaningFee = CleaningFee,
        DamageDeposit = DamageDeposit,
        Amenities = Amenities,
        HouseRules = HouseRules,
        AccommodationType = AccommodationType,
        Beds = Beds,
        AreaSqm = AreaSqm,
        ListingTitle = ListingTitle,
        PublishedOnSite = PublishedOnSite ?? true,
        CheckInFrom = CheckInFrom,
        CheckOutBy = CheckOutBy,
        SmokingPolicy = SmokingPolicy,
        PetsPolicy = PetsPolicy,
        PartiesPolicy = PartiesPolicy,
        ChildrenPolicy = ChildrenPolicy,
        QuietHoursFrom = QuietHoursFrom,
        QuietHoursTo = QuietHoursTo,
        ExtraGuestFeePerNight = ExtraGuestFeePerNight,
        CinCode = CinCode,
        TicinoStructureId = Casazen.Core.Regulatory.TicinoStructureId.Normalize(TicinoStructureId),
        Timezone = Timezone,
        CancellationPolicyId = CancellationPolicyId,
        CancellationFullRefundHours = CancellationFullRefundHours,
        CancellationPartialRefundHours = CancellationPartialRefundHours,
        CancellationPartialRefundPercent = CancellationPartialRefundPercent,
        CancellationRefundType = CancellationRefundType,
        Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim().ToLowerInvariant(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
