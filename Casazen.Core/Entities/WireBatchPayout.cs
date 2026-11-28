using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("WireBatchPayouts")]
public class WireBatchPayout : ITenantOwned
{
    public const string PayoutIndexName = "UIX_WireBatchPayouts_OwnerPayoutId";

    public const int EndToEndIdMaxLength = 35;

    public const string EndToEndIdPrefix = "CZ";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid WireBatchId { get; set; }

    public Guid OwnerPayoutId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Required]
    [MaxLength(EndToEndIdMaxLength)]
    public string EndToEndId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public static string EndToEndIdOf(Guid payoutId) => EndToEndIdPrefix + payoutId.ToString("N");
}
