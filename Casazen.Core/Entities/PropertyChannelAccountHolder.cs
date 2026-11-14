using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Who holds the account of one listing on one portal, when it is not the organization default (E14/F01/US01/T01, ⚖ I10,
/// <see cref="OrgFiscalIssuerProfile.PortalAccountHolder"/>): the owner (V1) or the agency (V2). One row for each property and portal (unique
/// <see cref="PropertyId"/>, <see cref="Channel"/>), gone with the property (cascade). Written as a list (<c>PUT api/billing-setup/channel-account-holders</c>)
/// and read through <c>IPortalAccountHolderResolver</c>: the statement labels (E13), the owner invoice (E14), the VAT summary (E15) and the TD17 (E17).
/// </summary>
[Table("PropertyChannelAccountHolders")]
public class PropertyChannelAccountHolder : ITenantOwned
{
    /// <summary>Unique (<see cref="PropertyId"/>, <see cref="Channel"/>): one holder for each listing and portal.</summary>
    public const string PropertyChannelIndexName = "UIX_PropertyChannelAccountHolders_PropertyId_Channel";

    /// <summary>The longest <see cref="Channel"/>: the name of a <see cref="BookingSource"/>.</summary>
    public const int ChannelMaxLength = 32;

    /// <summary>The longest <see cref="UpdatedByUserId"/>.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the property; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The listing (the property of the org).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>The portal, as the name of its <see cref="BookingSource"/> (<c>BookingCom</c>, <c>Airbnb</c>...): only the portals have a holder.</summary>
    [Required]
    [MaxLength(ChannelMaxLength)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Who holds the account of the listing on the portal.</summary>
    public PortalAccountHolder Holder { get; set; } = PortalAccountHolder.Owner;

    /// <summary>When the row was written last (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Who wrote it last.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
