using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.SiteProfile;
using Casazen.Core.Validation;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Properties")]
public class Property : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The user who created the property (the JWT <c>sub</c> of the caller). With a team it is no longer who may see it:
    /// the reach of a member comes from its org role and its <see cref="PropertyMemberAccess"/> rows (AM-03). It stays the
    /// reach of an account that is in no org team (a legacy owner), and the person notified while nobody is responsible.
    /// </summary>
    [Required, MaxLength(255)]
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>
    /// The member of the org in charge of the property (AM-03): the one who receives its notifications (new booking,
    /// supplier updates) together with the org's owner and administrators. <c>null</c> = nobody was named, and the creator
    /// (<see cref="OwnerId"/>) is notified instead. Always a user of the same org who reaches the property (checked when it is
    /// set), and back to <c>null</c> when that person loses the property or leaves the org (AM-03b); the notifications read the
    /// reach again anyway (<c>HostNotificationAudience</c>).
    /// </summary>
    [MaxLength(255)]
    public string? ResponsibleUserId { get; set; }

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Slug { get; set; }

    /// <summary>
    /// The random token in the address of the door page of the property (E22/F01/US02/T01, NF-24, NF-50): the printed QR code points to
    /// <c>{App:PublicSiteBaseUrl}/checkin/porta/{DoorToken}</c>, where the guest who arrives proves the stay with its booking code (or its surname) and the
    /// arrival date and is taken to the identity step of its check-in. 32 url-safe characters (<see cref="PropertyDoorTokens"/>), unique in the table and
    /// derived from nothing (the id of a property is enumerable, the token is not). Made the first time the host asks for the code; replaced by «Rigenera», and the
    /// printed code of the old one stops working at once. <c>null</c> for a property whose code was never asked. Never serialized with the property: only
    /// the host endpoint of the code returns it.
    /// </summary>
    [MaxLength(PropertyDoorTokens.MaxLength)]
    [JsonIgnore]
    public string? DoorToken { get; set; }

    [MaxLength(Casazen.Core.Services.ListingTextLimits.DescriptionMaxLength)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(PropertyAddress.UnitMaxLength)]
    public string? Unit { get; set; }

    [Required, MaxLength(50)]
    public string City { get; set; } = string.Empty;

    [MaxLength(10)]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// ISTAT code (6 digits) of the comune of the property, chosen by the host from the official list (<c>Comuni</c>, SU-04).
    /// Null until chosen: it is never inferred from the free-text <see cref="City"/>. It is the trusted comune of the
    /// tourist tax, of the regional rules and of the CIN check (<c>CinFormat.HasIstatComuneMismatch</c>), and it is cleared
    /// when the city is changed without choosing a comune.
    /// </summary>
    [MaxLength(Casazen.Core.Regulatory.ComuneRules.IstatCodeLength)]
    public string? ComuneIstatCode { get; set; }

    [MaxLength(10)]
    public string? RegionCode { get; set; }

    public decimal Latitude { get; set; }

    public decimal Longitude { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyBedroomsRange")]
    public int Bedrooms { get; set; }

    [Range(1, 50, ErrorMessage = "PropertyBathroomsRange")]
    public int Bathrooms { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyMaxGuestsRange")]
    public int MaxGuests { get; set; }

    [Precision(18, 2)]
    [Range(0, 100000, ErrorMessage = "PropertyNightlyRateRange")]
    public decimal NightlyRate { get; set; }

    [Range(PropertyStayRules.MinMinNights, PropertyStayRules.MaxMinNights, ErrorMessage = "PropertyMinNightsRange")]
    public int? MinNights { get; set; }

    /// <summary>
    /// Surcharge, in percent of <see cref="NightlyRate"/>, charged on the weekend nights of a stay (DB-03, D20): the nights
    /// of Friday and Saturday, by their Europe/Rome calendar date (<see cref="PropertyStayRules.IsWeekendNight"/>).
    /// <c>0</c> = no surcharge, the default of every property: each night costs <see cref="NightlyRate"/> and the price of a
    /// stay is what it has always been. The host chooses the value (the +15 % of the demo is an example, never imposed):
    /// 0 to <see cref="PropertyStayRules.MaxWeekendSurchargePercent"/>, two decimals. It does not apply to a night whose price
    /// the host confirmed for its date (a seasonal price, PC-15): that night costs that price (<see cref="Casazen.Core.Services.StayPricing"/>).
    /// </summary>
    [Precision(5, 2)]
    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyWeekendSurchargeRange")]
    public decimal WeekendSurchargePercent { get; set; }

    /// <summary>
    /// How the property is let (PM-01, D16 and D19): <see cref="Casazen.Core.Entities.Enums.RentalMode.Short"/> (short
    /// stays, the default: every row that existed before the column keeps working) or
    /// <see cref="Casazen.Core.Entities.Enums.RentalMode.Long"/> (long-term leases). The
    /// mode is exclusive. A <c>Long</c> property is never published (<c>PublicListing.IsPublished</c>), cannot be booked
    /// (422 <c>property_not_bookable_in_long_mode</c>) and is left out of the compliance status, the CIN alerts and
    /// summaries and the compliance cockpit; the plan limit (<c>MaxProperties</c>) still counts it. Set when the property
    /// is created (<see cref="Casazen.Core.Services.PropertyRentalModeRules.ResolveForCreation"/>) and by the scheduled
    /// mode change of PM-02, never by the generic update (<c>PropertyRepository.UpdateAsync</c> does not write it).
    /// Stored as an integer, append only.
    /// </summary>
    public RentalMode RentalMode { get; set; } = RentalMode.Short;

    [Precision(18, 2)]
    [Range(0, 10000, ErrorMessage = "PropertyCleaningFeeRange")]
    public decimal CleaningFee { get; set; }

    [Precision(18, 2)]
    [Range(0, 50000, ErrorMessage = "PropertyDamageDepositRange")]
    public decimal DamageDeposit { get; set; }

    public List<PropertyAmenity> Amenities { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();

    [MaxLength(1000)]
    public string HouseRules { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? GuestHouseGuideJson { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GuestPortalSetupJson { get; set; }

    // The fields of the wizard «Aggiungi immobile» (SR-07a). All optional: null = the host never said, which is every property
    // that existed before them, and nothing reads them to decide a booking, a price or a refund. Only PublishedOnSite changes
    // what the public site shows, and it starts true, so every property keeps the visibility it has today.

    public AccommodationType? AccommodationType { get; set; }

    [Range(PropertyHouseRules.MinBeds, PropertyHouseRules.MaxBeds, ErrorMessage = "PropertyBedsRange")]
    public int? Beds { get; set; }

    [Range(PropertyHouseRules.MinAreaSqm, PropertyHouseRules.MaxAreaSqm, ErrorMessage = "PropertyAreaSqmRange")]
    public int? AreaSqm { get; set; }

    [MaxLength(PropertyHouseRules.ListingTitleMaxLength)]
    public string? ListingTitle { get; set; }

    /// <summary>
    /// The host's switch «Mostra la casa sul sito» (SR-07a, SR-D24): <c>true</c> (the default, and the value of every property
    /// that existed before the column) = the property is on the booking site as soon as it can be; <c>false</c> = the host hides
    /// it. It is one of the conditions of <see cref="Casazen.Core.Services.PublicListing.IsPublished"/>, added to the others and
    /// never instead of them: turning it on publishes nothing that is not activated, and a paused, pending or suspended property
    /// stays off the site whatever this says.
    /// </summary>
    public bool PublishedOnSite { get; set; } = true;

    public TimeOnly? CheckInFrom { get; set; }

    public TimeOnly? CheckOutBy { get; set; }

    public SmokingPolicy? SmokingPolicy { get; set; }

    public PetsPolicy? PetsPolicy { get; set; }

    public PartiesPolicy? PartiesPolicy { get; set; }

    public ChildrenPolicy? ChildrenPolicy { get; set; }

    public TimeOnly? QuietHoursFrom { get; set; }

    public TimeOnly? QuietHoursTo { get; set; }

    [Precision(18, 2)]
    [Range(typeof(decimal), "0", "1000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "PropertyExtraGuestFeeRange")]
    public decimal? ExtraGuestFeePerNight { get; set; }

    // The site profile (DB-11a): optional facts and presentation texts the property page of the booking site shows when the host
    // filled them in (api/properties/{id}/site-profile). All optional: null = the host never said, which is every property that
    // existed before them; nothing here is read to decide a booking, a price or a refund, and none can hold an address or an exact
    // position. The fields of the wizard (SR-07a) above keep their one owner; the public page reads both.

    public int? FloorLevel { get; set; }

    public int? RenovatedYear { get; set; }

    [MaxLength(SitePropertyProfileLimits.EnergyClassMaxLength)]
    public string? EnergyClass { get; set; }

    [Precision(7, 2)]
    public decimal? EnergyIndex { get; set; }

    public PropertyAreaKind? AreaKind { get; set; }

    public bool? ChildrenWelcome { get; set; }

    public TimeOnly? CheckInTo { get; set; }

    [MaxLength(SitePropertyProfileLimits.EmbedUrlMaxLength)]
    public string? VideoUrl { get; set; }

    [MaxLength(SitePropertyProfileLimits.EmbedUrlMaxLength)]
    public string? TourUrl { get; set; }

    /// <summary>
    /// The texts the host wrote for the page, in Italian and English, and the short lists (the zone, what is near, the notes about the
    /// floor and the outdoor spaces, how check-in works, a note on the rules, up to three highlights, the safety notes, the labels
    /// of the site): a <see cref="SitePropertyProfileDocument"/> as JSON; null = nothing written. Read and written only through
    /// <see cref="SitePropertyProfileJson"/>, never edited by hand.
    /// </summary>
    [JsonIgnore]
    [Column(TypeName = "jsonb")]
    public string? SiteProfileJson { get; set; }

    public string? RequiredActivationPortalsJson { get; set; }

    [JsonIgnore]
    [Column(TypeName = "jsonb")]
    public string? PhotoAltJson { get; set; }

    [MaxLength(25)]
    [CinCode]
    public string? CinCode { get; set; }

    [MaxLength(PropertyCadastralLimits.SheetMaxLength)]
    public string? CadastralSheet { get; set; }

    [MaxLength(PropertyCadastralLimits.ParcelMaxLength)]
    public string? CadastralParcel { get; set; }

    [MaxLength(PropertyCadastralLimits.SubalternMaxLength)]
    public string? CadastralSubaltern { get; set; }

    [MaxLength(PropertyCadastralLimits.CategoryMaxLength)]
    public string? CadastralCategory { get; set; }

    [Precision(12, 2)]
    public decimal? CadastralIncome { get; set; }

    public bool HasCadastralData => IsCadastralDataComplete(CadastralSheet, CadastralParcel, CadastralCategory, CadastralIncome);

    public static bool IsCadastralDataComplete(string? sheet, string? parcel, string? category, decimal? income) =>
        !string.IsNullOrWhiteSpace(sheet)
        && !string.IsNullOrWhiteSpace(parcel)
        && !string.IsNullOrWhiteSpace(category)
        && income is not null;

    [Required, MaxLength(2)]
    public string Country { get; set; } = "IT";

    [Required, MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    [MaxLength(50)]
    public string Timezone { get; set; } = "Europe/Rome";

    [MaxLength(10)]
    public string? SwissMunicipalityCode { get; set; }

    [MaxLength(Regulatory.TicinoStructureId.MaxLength)]
    public string? TicinoStructureId { get; set; }

    public GuestNotificationOverride? GuestNotificationOverride { get; set; }

    [ForeignKey("CancellationPolicy")]
    public Guid? CancellationPolicyId { get; set; }
    public virtual CancellationPolicy? CancellationPolicy { get; set; }

    public int? CancellationFullRefundHours { get; set; }

    public int? CancellationPartialRefundHours { get; set; }

    [Precision(18, 2)]
    public decimal? CancellationPartialRefundPercent { get; set; }

    public HostCancellationRefundType CancellationRefundType { get; set; } = HostCancellationRefundType.Percent;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Host-set pause (PC-03, A2-05): reversible and temporary — the property stays fully visible and editable to its
    /// host, keeps its plan slot (the entitlement count ignores it), and its existing bookings are untouched, but it
    /// is hidden from public search, its public page and new guest bookings (<c>PublicListing.IsPublished</c>) until
    /// the host reactivates it. Distinct from a soft delete (PC-05, A2-18, <c>IsDeleted</c>/<c>DeletedAt</c>): pausing never
    /// removes or hides the property from its own host.
    /// </summary>
    public bool IsPaused { get; set; }

    public DateTime? PausedAt { get; set; }

    public bool IsTestData { get; set; }

    public PropertyComplianceStatus ComplianceStatus { get; set; } = PropertyComplianceStatus.Pending;

    public DateTime? ComplianceCompletedAt { get; set; }

    /// <summary>
    /// UTC instant the property went from <see cref="PropertyComplianceStatus.Active"/> to
    /// <see cref="PropertyComplianceStatus.Suspended"/> because an activation blocker appeared (CO-06, A5-20); null when
    /// it is not suspended. Cleared by the reactivation.
    /// </summary>
    public DateTime? ComplianceSuspendedAt { get; set; }

    public List<string>? ComplianceSuspensionReasons { get; set; }

    /// <summary>
    /// UTC instant of the last evaluation of the status of an active or suspended property by the compliance status
    /// service (CO-06).
    /// Null = never evaluated: the property was published before CO-06 (backfill of A5-36 or old checklist), and its first
    /// evaluation follows <c>Compliance:StatusCheck:NotifyOnFirstCheck</c> for the email to the host.
    /// </summary>
    public DateTime? ComplianceCheckedAt { get; set; }

    /// <summary>
    /// Codice fiscale of the taxpayer who lets this apartment (titolare fiscale, CO-18): the short-rental threshold and the
    /// one 21% cedolare unit are per taxpayer, not per org (fiscale.md C4). Null: the org's own tax profile. Normalized
    /// (16 characters, upper case). Personal data: never serialized with the property, only masked by the fiscal API.
    /// </summary>
    [MaxLength(16)]
    [JsonIgnore]
    public string? TaxpayerFiscalCode { get; set; }

    // The D.L. 145/2023 safety checklist lives in PropertySafetyChecklists (CO-07): the old JSON column was migrated there.

    /// <summary>
    /// Soft delete (PC-05, A2-18): set instead of removing the row, so fiscal history tied to the property (tourist
    /// tax reports, CIN, Alloggiati communications, cedolare secca / <see cref="PropertyFiscalYear"/>) stays intact for
    /// Italian compliance retention. Excluded from every normal read by the <c>SoftDelete</c> global query filter
    /// (<c>AppDbContext.SoftDeleteQueryFilter</c>); reporting/compliance code reaches a deleted property explicitly with
    /// <c>IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])</c>. Never physically deleted.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public virtual ICollection<OtaIntegration> OtaIntegrations { get; set; } = new List<OtaIntegration>();
    public virtual ICollection<PropertyDocument> PropertyDocuments { get; set; } = new List<PropertyDocument>();
    public virtual PricingAdapterConfig? PricingAdapterConfig { get; set; }

    /// <summary>
    /// The members «Solo alcuni» who reach this property (AM-03). Never loaded with the property: it exists so that the list
    /// filters can say <c>EXISTS</c> in SQL (<c>HostScopeQueryExtensions.InScope</c>).
    /// </summary>
    public virtual ICollection<PropertyMemberAccess> MemberAccesses { get; set; } = new List<PropertyMemberAccess>();
}

public static class PropertyCadastralLimits
{
    public const int SheetMaxLength = 10;
    public const int ParcelMaxLength = 20;
    public const int SubalternMaxLength = 10;
    public const int CategoryMaxLength = 10;
    public const double IncomeMax = 10_000_000;
}
