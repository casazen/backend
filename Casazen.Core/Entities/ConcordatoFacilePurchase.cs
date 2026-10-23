using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("ConcordatoFacilePurchases")]
public class ConcordatoFacilePurchase : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid LeaseContractId { get; set; }
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required, MaxLength(255)]
    public string StripeCheckoutSessionId { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    public int AmountCents { get; set; }

    [Required, MaxLength(3)]
    public string Currency { get; set; } = string.Empty;

    public ConcordatoFacilePurchaseStatus Status { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }

    public DateTime? RefundedAt { get; set; }
}
