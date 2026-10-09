using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Validation;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Properties")]
public class Property : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Tenant key (AC2). Server-set from the caller's org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>URL-friendly identifier unique within the org for direct booking links.</summary>
    [MaxLength(100)]
    public string? Slug { get; set; }

    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Interno / scala of the apartment inside the building (PC-06, A2-19): free text such as <c>int. 5</c> or
    /// <c>Scala B int. 5</c>, trimmed with inner spaces collapsed (<see cref="PropertyAddress.NormalizeUnit"/>). Null for
    /// a property that has no unit (a house, the only apartment of the address). Part of the address uniqueness: several
    /// apartments of the same building are different properties of the org.
    /// </summary>
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

    /// <summary>
    /// CasaZen's code of the region (<c>LOM</c>, <c>LAZ</c>) of <see cref="ComuneIstatCode"/>, set together with it from the
    /// official list; the key of <c>Compliance:RequiredDocuments</c>. Null while no comune is chosen.
    /// </summary>
    [MaxLength(10)]
    public string? RegionCode { get; set; }

    /// <summary>
    /// WGS84 latitude in degrees, -90 to 90, stored with <see cref="PropertyAddress.CoordinateScale"/> decimals (about
    /// 0.1 m, PC-06, A2-33). <c>0</c> together with a longitude of <c>0</c> = not set.
    /// </summary>
    public decimal Latitude { get; set; }

    /// <summary>WGS84 longitude in degrees, -180 to 180, same precision as <see cref="Latitude"/>.</summary>
    public decimal Longitude { get; set; }

    [Range(0, 100, ErrorMessage = "PropertyBedroomsRange")]
    public int Bedrooms { get; set; }

    [Range(1, 50, ErrorMessage = "PropertyBathroomsRange")]
    public int Bathrooms { get; set; }

    /// <summary>
    /// Short-stay guests; <c>0</c> = not set. Before PM-01 this and <see cref="NightlyRate"/> at <c>0</c> were the only,
    /// implicit marker of a long-term property (A7-06); the marker is now <see cref="RentalMode"/>.
    /// </summary>
    [Range(0, 100, ErrorMessage = "PropertyMaxGuestsRange")]
    public int MaxGuests { get; set; }

    /// <summary>Short-stay nightly rate; <c>0</c> = none (see <see cref="MaxGuests"/>).</summary>
    [Precision(18, 2)]
    [Range(0, 100000, ErrorMessage = "PropertyNightlyRateRange")]
    public decimal NightlyRate { get; set; }

    /// <summary>
    /// Fewest nights the public booking site accepts for a stay (DB-03): <c>null</c> = no minimum, which is what every
    /// property had before the column existed. When set it is between <see cref="PropertyStayRules.MinMinNights"/> and
    /// <see cref="PropertyStayRules.MaxMinNights"/> (30 nights is the longest short-term let). It is a rule of the guests'
    /// quote and checkout (422 <c>direct_booking_min_nights_not_met</c>), not of the host: a stay the host enters by hand can
    /// be shorter.
    /// </summary>
    [Range(PropertyStayRules.MinMinNights, PropertyStayRules.MaxMinNights, ErrorMessage = "PropertyMinNightsRange")]
    public int? MinNights { get; set; }

    /// <summary>
    /// Surcharge, in percent of <see cref="NightlyRate"/>, charged on the weekend nights of a stay (DB-03, D20): the nights
    /// of Friday and Saturday, by their Europe/Rome calendar date (<see cref="PropertyStayRules.IsWeekendNight"/>).
    /// <c>0</c> = no surcharge, the default of every property: each night costs <see cref="NightlyRate"/> and the price of a
    /// stay is what it has always been. The host chooses the value (the +15 % of the demo is an example, never imposed):
    /// 0 to <see cref="PropertyStayRules.MaxWeekendSurchargePercent"/>, two decimals.
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

    // Italian regulatory compliance - D.L. 145/2023
    [MaxLength(25)]
    [CinCode]
    public string? CinCode { get; set; } // Normalized CIN, e.g. IT058091C27G5FFZDZ (see CinFormat)

    // Cadastral identification of the unit (catasto fabbricati), used by the lease contract (LT-03, LT-10). Free text
    // within a length: the formats are not validated beyond that (no invented patterns).

    /// <summary>Foglio: also finds the canone concordato zone of comuni zoned by sheet (LT-10).</summary>
    [MaxLength(PropertyCadastralLimits.SheetMaxLength)]
    public string? CadastralSheet { get; set; }

    /// <summary>Particella (mappale).</summary>
    [MaxLength(PropertyCadastralLimits.ParcelMaxLength)]
    public string? CadastralParcel { get; set; }

    /// <summary>Subalterno; some units have none.</summary>
    [MaxLength(PropertyCadastralLimits.SubalternMaxLength)]
    public string? CadastralSubaltern { get; set; }

    /// <summary>Categoria catastale as written in the visura (e.g. "A/2").</summary>
    [MaxLength(PropertyCadastralLimits.CategoryMaxLength)]
    public string? CadastralCategory { get; set; }

    /// <summary>Rendita catastale in euros.</summary>
    [Precision(12, 2)]
    public decimal? CadastralIncome { get; set; }

    /// <summary>
    /// Complete for the contract: sheet, parcel, category and income (the subaltern is optional, LT-10).
    /// </summary>
    public bool HasCadastralData =>
        !string.IsNullOrWhiteSpace(CadastralSheet)
        && !string.IsNullOrWhiteSpace(CadastralParcel)
        && !string.IsNullOrWhiteSpace(CadastralCategory)
        && CadastralIncome is not null;

    // Timezone for booking date handling (IANA timezone ID)
    [MaxLength(50)]
    public string Timezone { get; set; } = "Europe/Rome"; // Default for Italian properties

    [ForeignKey("CancellationPolicy")]
    public Guid? CancellationPolicyId { get; set; }
    public virtual CancellationPolicy? CancellationPolicy { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Host-set pause (PC-03, A2-05): reversible and temporary — the property stays fully visible and editable to its
    /// host, keeps its plan slot (the entitlement count ignores it), and its existing bookings are untouched, but it
    /// is hidden from public search, its public page and new guest bookings (<c>PublicListing.IsPublished</c>) until
    /// the host reactivates it. Distinct from a soft delete (PC-05, A2-18, <c>IsDeleted</c>/<c>DeletedAt</c>): pausing never
    /// removes or hides the property from its own host.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>UTC instant the host paused the property (<see cref="IsPaused"/>); null when not paused. Cleared on reactivation.</summary>
    public DateTime? PausedAt { get; set; }

    /// <summary>Compliance activation gate for public listing (US-019 / #295).</summary>
    public PropertyComplianceStatus ComplianceStatus { get; set; } = PropertyComplianceStatus.Pending;

    public DateTime? ComplianceCompletedAt { get; set; }

    /// <summary>
    /// UTC instant the property went from <see cref="PropertyComplianceStatus.Active"/> to
    /// <see cref="PropertyComplianceStatus.Suspended"/> because an activation blocker appeared (CO-06, A5-20); null when
    /// it is not suspended. Cleared by the reactivation.
    /// </summary>
    public DateTime? ComplianceSuspendedAt { get; set; }

    /// <summary>
    /// Stable codes of the activation blockers that suspended the property (e.g. <c>activation_cin_missing</c>,
    /// <c>safety_confirmation_missing</c>), as they were at the suspension; null when it is not suspended.
    /// </summary>
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

    /// <summary>UTC instant the property was soft-deleted (PC-05); null while it is not.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public virtual ICollection<OtaIntegration> OtaIntegrations { get; set; } = new List<OtaIntegration>();
    public virtual ICollection<PropertyDocument> PropertyDocuments { get; set; } = new List<PropertyDocument>();
    public virtual PricingAdapterConfig? PricingAdapterConfig { get; set; }
}

/// <summary>Lengths of the cadastral fields of <see cref="Property"/> (LT-10).</summary>
public static class PropertyCadastralLimits
{
    public const int SheetMaxLength = 10;
    public const int ParcelMaxLength = 20;
    public const int SubalternMaxLength = 10;
    public const int CategoryMaxLength = 10;
    public const double IncomeMax = 10_000_000;
}
