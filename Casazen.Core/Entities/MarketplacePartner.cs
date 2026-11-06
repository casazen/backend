using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// A partner of the marketplace's Partners group (E09/F01/US01, PO-11, NF-59): an entry of the catalog the platform admin curates, with
/// its order, its visibility by plan and by country, and the optional link to the portal Octorate lists for it. Platform data, the same
/// catalog for every org (no <c>OrgId</c>); which partner an org uses is <see cref="MarketplaceActivation"/>.
/// </summary>
/// <remarks>
/// A partner is a row entered by the admin: no partner name lives in the code or in a migration (AC-NF54-5). The words it shows to the
/// host are <see cref="MarketplacePartnerText"/>, one row per language. A partner is hidden until the admin makes it visible
/// (<see cref="IsVisible"/> defaults to false).
/// </remarks>
[Table("MarketplacePartners")]
public class MarketplacePartner
{
    /// <summary>Name of the unique index on <see cref="Code"/>.</summary>
    public const string CodeIndexName = "UIX_MarketplacePartners_Code";

    /// <summary>Name of the check that keeps <see cref="Code"/> a lowercase slug.</summary>
    public const string CodeSlugCheckName = "CK_MarketplacePartners_CodeSlug";

    /// <summary>Name of the check that keeps <see cref="WebsiteUrl"/> an https address.</summary>
    public const string WebsiteHttpsCheckName = "CK_MarketplacePartners_WebsiteHttps";

    public const int CodeMaxLength = 60;
    public const int NameMaxLength = 120;
    public const int WebsiteUrlMaxLength = 500;
    public const int LogoKeyMaxLength = 300;
    public const int OctoratePortalCodeMaxLength = 100;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>A lowercase slug (<c>a-z</c>, <c>0-9</c>, single hyphens inside): the code the activation and the detail address point at. Unique.</summary>
    [Required]
    [MaxLength(CodeMaxLength)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The work it does for the host, which orders the Partners group.</summary>
    public MarketplacePartnerJob Job { get; set; } = MarketplacePartnerJob.Sell;

    /// <summary>The place among the partners of the same <see cref="Job"/>, the lower first.</summary>
    public int SortOrder { get; set; }

    /// <summary>False until the admin shows the partner: a hidden partner is never returned by any endpoint of the marketplace.</summary>
    public bool IsVisible { get; set; }

    /// <summary>The partner's site (an https address), where the host activates it.</summary>
    [Required]
    [MaxLength(WebsiteUrlMaxLength)]
    public string WebsiteUrl { get; set; } = string.Empty;

    /// <summary>The key of the partner's logo in the public bucket; <c>null</c> when it has none.</summary>
    [MaxLength(LogoKeyMaxLength)]
    public string? LogoKey { get; set; }

    /// <summary>The portal code of the same partner in Octorate's catalog, when it is there: the optional link.</summary>
    [MaxLength(OctoratePortalCodeMaxLength)]
    public string? OctoratePortalCode { get; set; }

    /// <summary>Who invoices the cost to the host.</summary>
    public MarketplaceCostInvoicer InvoicedBy { get; set; } = MarketplaceCostInvoicer.Unknown;

    /// <summary>The codes of <c>GET api/service-categories</c> the partner works for (text array); empty = none.</summary>
    public List<string> ServiceCategories { get; set; } = [];

    /// <summary>The plans the partner is shown to (integer array of <see cref="PlanTier"/>); empty = every plan.</summary>
    public PlanTier[] VisiblePlanTiers { get; set; } = [];

    /// <summary>
    /// The countries the partner is shown to: ISO 3166-1 alpha-2 in upper case (text array); empty = every country. An org with no country
    /// sees only a partner with no country restriction.
    /// </summary>
    public List<string> VisibleCountries { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The platform admin who last changed the row.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>The texts of the partner, one row per language (Italian and English).</summary>
    public ICollection<MarketplacePartnerText> Texts { get; set; } = [];
}
