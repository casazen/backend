using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OrgChannelCommissionRules")]
public class OrgChannelCommissionRule : ITenantOwned
{
    public const string ChannelValidFromIndexName = "UIX_OrgChannelCommissionRules_OrgId_Channel_ValidFrom";

    public const int ChannelMaxLength = 32;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(ChannelMaxLength)]
    public string Channel { get; set; } = string.Empty;

    [Column(TypeName = "decimal(5,2)")]
    public decimal Percent { get; set; }

    public CommissionBase Base { get; set; } = CommissionBase.GrossExclTouristTax;

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
