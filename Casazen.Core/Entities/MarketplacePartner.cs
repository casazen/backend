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
    public const string CodeIndexName = "UIX_MarketplacePartners_Code";

    public const string CodeSlugCheckName = "CK_MarketplacePartners_CodeSlug";

    public const string WebsiteHttpsCheckName = "CK_MarketplacePartners_WebsiteHttps";

    public const int CodeMaxLength = 60;
    public const int NameMaxLength = 120;
    public const int WebsiteUrlMaxLength = 500;
    public const int LogoKeyMaxLength = 300;
    public const int OctoratePortalCodeMaxLength = 100;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(CodeMaxLength)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    public MarketplacePartnerJob Job { get; set; } = MarketplacePartnerJob.Sell;

    public int SortOrder { get; set; }

    public bool IsVisible { get; set; }

    [Required]
    [MaxLength(WebsiteUrlMaxLength)]
    public string WebsiteUrl { get; set; } = string.Empty;

    [MaxLength(LogoKeyMaxLength)]
    public string? LogoKey { get; set; }

    [MaxLength(OctoratePortalCodeMaxLength)]
    public string? OctoratePortalCode { get; set; }

    public MarketplaceCostInvoicer InvoicedBy { get; set; } = MarketplaceCostInvoicer.Unknown;

    public List<string> ServiceCategories { get; set; } = [];

    public PlanTier[] VisiblePlanTiers { get; set; } = [];

    public List<string> VisibleCountries { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    public ICollection<MarketplacePartnerText> Texts { get; set; } = [];
}
