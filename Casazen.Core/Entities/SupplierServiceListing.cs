using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// One service of a supplier's price catalog (SP-02, <c>api/supplier/services</c>): name, category, "from" price or on
/// quote, duration, supplements, what is included and excluded, photos, the weekdays it is offered on and the minimum
/// notice, in <see cref="SupplierServiceListingStatus.Draft"/>, <see cref="SupplierServiceListingStatus.Active"/> or
/// <see cref="SupplierServiceListingStatus.Paused"/>.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> Keyed by the supplier org (<see cref="OrgId"/> = <see cref="SupplierProfile.OrgId"/>), like
/// <see cref="SupplierProfile"/> and <see cref="SupplierAvailability"/>: it is <b>not</b> <c>ITenantOwned</c>. A supplier
/// acts as <c>User.SupplierOrgId</c>, and a supplier-only account has no <c>User.OrgId</c>, so the global tenant filter
/// (host org) would give it no rows. It is listed in the allow-list of <c>TenantQueryFilterArchitectureTests</c> and every
/// read and write filters by <see cref="OrgId"/> explicitly (<c>SupplierServiceCatalogService</c> only).</para>
/// <para><b>Deleting.</b> Soft: <see cref="DeletedAt"/> hides the row everywhere and frees its slug (the unique index
/// covers the rows that are not deleted); nothing is removed, so the service requests that will point to a listing
/// (SP-04) keep their reference.</para>
/// <para><b>Concurrency.</b> <see cref="Version"/> is PostgreSQL's <c>xmin</c>: an update must carry the version the
/// client read, otherwise it is refused with 409.</para>
/// </remarks>
[Table("SupplierServiceListings")]
public class SupplierServiceListing
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The supplier org the service belongs to (<see cref="SupplierProfile.OrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    /// <summary>
    /// Last part of the public address of the service (<c>/fornitori/{supplierSlug}/servizi/{slug}</c>), unique among the
    /// supplier's services that are not deleted. Taken from the name when the service is created and kept from then on;
    /// only a draft, which was never public, follows a change of its name.
    /// </summary>
    [Required, MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string Slug { get; set; } = string.Empty;

    [Required, MaxLength(SupplierServiceCatalogLimits.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Code of <see cref="ServiceCategories"/>.</summary>
    [Required, MaxLength(SupplierServiceCatalogLimits.CategoryMaxLength)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(SupplierServiceCatalogLimits.SummaryMaxLength)]
    public string? Summary { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>
    /// The "from" price in cents, in euro. <c>null</c> is "on quote": the price is agreed before the work
    /// (<see cref="RequiresQuote"/>); a draft may not have one yet.
    /// </summary>
    public int? PriceFromCents { get; set; }

    /// <summary>What <see cref="PriceFromCents"/> is for (per job, hour, set or square meter).</summary>
    public SupplierServicePriceUnit PriceUnit { get; set; } = SupplierServicePriceUnit.PerJob;

    /// <summary>
    /// Whether the prices of the service (the base price and the supplements) already include VAT (decision D4: one price
    /// with a flag). False until the supplier says so: CasaZen promises nothing about VAT on its behalf.
    /// </summary>
    public bool PricesIncludeVat { get; set; }

    /// <summary>
    /// The customer asks and waits for the supplier's offer: the price is confirmed before the work. A service with this
    /// flag can be published without a price.
    /// </summary>
    public bool RequiresQuote { get; set; }

    /// <summary>Indicative duration in minutes; the slot planner uses it (SP-03). Required to publish.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>
    /// Shortest notice, in hours, between the booking and the work. <c>null</c>: the supplier's default notice (SP-03).
    /// <c>0</c>: no notice.
    /// </summary>
    public int? MinNoticeHours { get; set; }

    /// <summary>
    /// Weekdays the service is offered on, as a bit mask (<see cref="SupplierServiceWeekdays"/>: bit 0 Monday ... bit 6
    /// Sunday). On top of the supplier's working hours, never instead of them.
    /// </summary>
    public int WeekdaysMask { get; set; } = SupplierServiceWeekdays.AllMask;

    /// <summary>
    /// JSON array of <see cref="SupplierServiceSupplement"/> (<c>code</c>, <c>label</c>, <c>amountCents</c>, <c>per</c>,
    /// <c>max</c>): structured, because the price estimate of the booking (SP-09) is computed from them.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string SupplementsJson { get; set; } = "[]";

    /// <summary>JSON array of strings: what the price includes.</summary>
    [Column(TypeName = "jsonb")]
    public string IncludedJson { get; set; } = "[]";

    /// <summary>JSON array of strings: what the price does not include.</summary>
    [Column(TypeName = "jsonb")]
    public string ExcludedJson { get; set; } = "[]";

    /// <summary>JSON array of absolute photo URLs in display order (the first is the cover), at most <see cref="SupplierServiceCatalogLimits.MaxPhotos"/>.</summary>
    [Column(TypeName = "jsonb")]
    public string PhotoUrlsJson { get; set; } = "[]";

    public SupplierServiceListingStatus Status { get; set; } = SupplierServiceListingStatus.Draft;

    /// <summary>Position in the catalog (ascending, then by creation).</summary>
    public int SortOrder { get; set; }

    /// <summary>UTC moment the supplier deleted the service (soft delete); <c>null</c> while it exists.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of
    /// the row (no column of its own, as on <see cref="ServiceRequest"/>). The update of a service must carry the version
    /// the client read.
    /// </summary>
    public uint Version { get; set; }

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
