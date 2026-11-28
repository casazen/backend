using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OpenapiLeaseRegistrationPayments")]
public class OpenapiLeaseRegistrationPayment : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid LeaseContractId { get; set; }
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    public decimal FeePercent { get; set; }

    public int AmountCents { get; set; }

    [Required, MaxLength(3)]
    public string Currency { get; set; } = "eur";

    public OpenapiRegistrationPaymentStatus Status { get; set; } = OpenapiRegistrationPaymentStatus.Pending;

    [Required, MaxLength(255)]
    public string StripeCheckoutSessionId { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? SessionUrl { get; set; }

    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }
}
