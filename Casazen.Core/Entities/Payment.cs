using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Payments")]
public class Payment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Tenant key (AC2). Inherited from the payment's booking; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Precision(18, 2)]
    public decimal Amount { get; set; }

    /// <summary>
    /// Total amount refunded so far (for partial refunds tracking)
    /// </summary>
    [Precision(18, 2)]
    public decimal RefundedAmount { get; set; } = 0m;

    [Required]
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    [Required]
    public PaymentMethod Method { get; set; } = PaymentMethod.CreditCard;

    [MaxLength(500)]
    public string TransactionId { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PaymentStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Refunded,
    PartiallyRefunded
}

public enum PaymentMethod
{
    CreditCard,
    BankTransfer,
    PayPal,
    ApplePay,
    GooglePay
}
