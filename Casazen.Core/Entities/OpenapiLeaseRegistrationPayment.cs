using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The filing fee of one Openapi registration of a lease that is not canone concordato.
/// <see cref="FeePercent"/> and <see cref="AmountCents"/> are snapshots of the quote at checkout: a later change of the
/// rate does not rewrite a payment, and the gate accepts only a completed payment of the quote in force now.
/// </summary>
[Table("OpenapiLeaseRegistrationPayments")]
public class OpenapiLeaseRegistrationPayment : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid LeaseContractId { get; set; }
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    /// <summary>The registration-tax rate in force at checkout (for example 0.02).</summary>
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
