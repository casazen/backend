using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// A portal or tool of the channel manager's catalog, as the daily refresh last read it (E09/F01/US01, NF-54): the sales channels and the
/// connected tools of the marketplace. Platform reference data, the same for every org (no <c>OrgId</c>): the marketplace page reads this
/// table and never calls Octorate while it loads (AC-NF54-4).
/// </summary>
/// <remarks>
/// <para>Only what Octorate says is stored, and nothing is written by hand: no portal name lives in the code or in a migration. A portal
/// that a refresh no longer returns is kept with <see cref="IsListed"/> false, so the activations that point at it keep their name.</para>
/// <para>No personal data and no secret: the flags that decide what the activation needs (<see cref="RequiresHotelId"/>,
/// <see cref="RequiresPassword"/>, <see cref="RequiresTokenAuthentication"/>) and how the commission is collected.</para>
/// </remarks>
[Table("MarketplacePortals")]
public class MarketplacePortal
{
    public const string PortalCodeIndexName = "UIX_MarketplacePortals_PortalCode";

    public const int PortalCodeMaxLength = 100;
    public const int NameMaxLength = 200;
    public const int UrlMaxLength = 500;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(PortalCodeMaxLength)]
    public string PortalCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    public MarketplacePortalCategory Category { get; set; } = MarketplacePortalCategory.Ota;

    public bool Creatable { get; set; }

    public bool Enabled { get; set; }

    public bool Preferred { get; set; }

    [MaxLength(UrlMaxLength)]
    public string? Website { get; set; }

    [MaxLength(UrlMaxLength)]
    public string? LogoUrl { get; set; }

    public bool RequiresHotelId { get; set; }

    public bool RequiresPassword { get; set; }

    public bool RequiresTokenAuthentication { get; set; }

    public bool FeatureContent { get; set; }

    public bool OctorateManageable { get; set; }

    public bool CommissionIncludedInPayments { get; set; }

    public bool? CommissionsInvoiced { get; set; }

    public bool IsListed { get; set; } = true;

    public DateTime RefreshedAt { get; set; } = DateTime.UtcNow;
}
