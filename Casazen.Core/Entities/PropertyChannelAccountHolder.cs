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
    public const string PropertyChannelIndexName = "UIX_PropertyChannelAccountHolders_PropertyId_Channel";

    public const int ChannelMaxLength = 32;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    [Required]
    [MaxLength(ChannelMaxLength)]
    public string Channel { get; set; } = string.Empty;

    public PortalAccountHolder Holder { get; set; } = PortalAccountHolder.Owner;

    public DateTime UpdatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
