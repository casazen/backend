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
    /// <summary>Name of the unique index on <see cref="PortalCode"/>.</summary>
    public const string PortalCodeIndexName = "UIX_MarketplacePortals_PortalCode";

    public const int PortalCodeMaxLength = 100;
    public const int NameMaxLength = 200;
    public const int UrlMaxLength = 500;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The identifier Octorate uses for the portal in the calls about its connections. Unique.</summary>
    [Required]
    [MaxLength(PortalCodeMaxLength)]
    public string PortalCode { get; set; } = string.Empty;

    /// <summary>The name of the portal as Octorate gives it.</summary>
    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    public MarketplacePortalCategory Category { get; set; } = MarketplacePortalCategory.Ota;

    /// <summary>Octorate lets a connection to the portal be created through its API.</summary>
    public bool Creatable { get; set; }

    /// <summary>Octorate offers the portal at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Octorate lists the portal first (a portal it wants to promote).</summary>
    public bool Preferred { get; set; }

    /// <summary>The site of the portal (an https address), when Octorate gives one.</summary>
    [MaxLength(UrlMaxLength)]
    public string? Website { get; set; }

    /// <summary>The logo of the portal (an https address), when Octorate gives one.</summary>
    [MaxLength(UrlMaxLength)]
    public string? LogoUrl { get; set; }

    /// <summary>The connection needs the identifier of the property on the portal (<c>requiresHotelId</c>).</summary>
    public bool RequiresHotelId { get; set; }

    /// <summary>The connection needs the username and password of the portal (<c>requiresPassword</c>).</summary>
    public bool RequiresPassword { get; set; }

    /// <summary>The host has to give the access on the channel manager's own panel (<c>requiresTokenAuthentication</c>).</summary>
    public bool RequiresTokenAuthentication { get; set; }

    /// <summary>The portal carries the content and the rates of the listing, not only availability, prices and bookings (<c>featureContent</c>).</summary>
    public bool FeatureContent { get; set; }

    /// <summary>The channel manager can manage the listing on the portal itself (<c>octorateManageable</c>).</summary>
    public bool OctorateManageable { get; set; }

    /// <summary>The portal pays the host the amount net of its commission (<c>commissionIncludedInPayments</c>).</summary>
    public bool CommissionIncludedInPayments { get; set; }

    /// <summary>
    /// The portal invoices its commission to the host separately (<c>commissionsInvoiced</c>); <c>null</c> when Octorate does not say, which is
    /// not the same as no. Read by the channel costs report.
    /// </summary>
    public bool? CommissionsInvoiced { get; set; }

    /// <summary>False when the latest refresh no longer returned the portal: it is not offered any more, its row is kept.</summary>
    public bool IsListed { get; set; } = true;

    /// <summary>When a refresh last read the portal from Octorate (UTC).</summary>
    public DateTime RefreshedAt { get; set; } = DateTime.UtcNow;
}
