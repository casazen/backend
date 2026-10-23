using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("ConcordatoFacileGifts")]
public class ConcordatoFacileGift : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required, MaxLength(255)]
    public string GrantedBySubscriptionId { get; set; } = string.Empty;

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ConsumedAt { get; set; }

    public Guid? ConsumedForLeaseId { get; set; }
    public virtual LeaseContract? ConsumedForLease { get; set; }

    public ConcordatoFacileGiftStatus Status { get; set; }
}
