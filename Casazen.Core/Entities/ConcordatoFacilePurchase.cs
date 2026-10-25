using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Tracks one-time payment (EUR 119,00) for ConcordatoFacile service access per lease (ADR-004).
/// Protects the associated draft from deletion while the purchase is pending or completed.
/// </summary>
[Table("ConcordatoFacilePurchases")]
public class ConcordatoFacilePurchase : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ITenantOwned: organization that owns this purchase.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The lease draft this purchase is for.</summary>
    public Guid LeaseContractId { get; set; }
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    /// <summary>Stripe checkout session ID for idempotency.</summary>
    [Required, MaxLength(255)]
    public string StripeCheckoutSessionId { get; set; } = string.Empty;

    /// <summary>Stripe payment intent ID (nullable, populated on payment completion).</summary>
    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    /// <summary>Amount in cents (11900 for EUR 119,00).</summary>
    public int AmountCents { get; set; }

    /// <summary>Currency code (always "eur" for CF).</summary>
    [Required, MaxLength(3)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>Purchase status (Pending/Completed/Failed/Refunded).</summary>
    public ConcordatoFacilePurchaseStatus Status { get; set; }

    /// <summary>When the checkout session was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the payment was completed (nullable).</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>When the payment was refunded (nullable).</summary>
    public DateTime? RefundedAt { get; set; }
}
