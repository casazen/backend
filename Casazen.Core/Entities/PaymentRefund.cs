using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One refund of a <see cref="Payment"/> made on Stripe (BK-02, A3-05). The row is written as
/// <see cref="PaymentRefundStatus.Pending"/> before Stripe is called, so its id is the idempotency key of the Stripe
/// request and a retry never refunds twice. Only Stripe moves it to <see cref="PaymentRefundStatus.Succeeded"/> (the
/// synchronous response or the <c>refund.*</c> / <c>charge.refunded</c> webhooks), and only succeeded refunds count in
/// <see cref="Payment.RefundedAmount"/> and in the Refunded / PartiallyRefunded status of the payment.
/// </summary>
[Table("PaymentRefunds")]
public class PaymentRefund : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Payment))]
    public Guid PaymentId { get; set; }
    public virtual Payment Payment { get; set; } = null!;

    public Guid OrgId { get; set; }

    [Precision(18, 2)]
    public decimal Amount { get; set; }

    public PaymentRefundStatus Status { get; set; } = PaymentRefundStatus.Pending;

    public PaymentRefundOrigin Origin { get; set; } = PaymentRefundOrigin.Host;

    [MaxLength(255)]
    public string? StripeRefundId { get; set; }

    /// <summary>Idempotency key sent to Stripe with the create request (<c>payment-refund:{Id}</c>).</summary>
    [Required, MaxLength(255)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? FailureReason { get; set; }

    /// <summary>Host note (why the refund was made). Never sent to Stripe.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    [MaxLength(255)]
    public string? RequestedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    public DateTime? GuestNotifiedAt { get; set; }
}

public enum PaymentRefundStatus
{
    Pending,
    Succeeded,
    Failed,
    Canceled,
    RequiresAction,
}

public enum PaymentRefundOrigin
{
    Host,

    BookingCancellation,

    Stripe,
}
