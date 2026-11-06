using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Payments")]
public class Payment : ITenantOwned
{
    /// <summary>Longest <see cref="OctorateTransactionId"/> (varchar 100).</summary>
    public const int OctorateTransactionIdMaxLength = 100;

    /// <summary>
    /// Name of the unique index on (<see cref="OrgId"/>, <see cref="OctorateTransactionId"/>), filtered to the payments recorded in Octorate:
    /// a payment is recorded on its reservation once (E08/F01/US01/T01, AC-NF74-4).
    /// </summary>
    public const string OctorateTransactionIndexName = "UIX_Payments_OrgId_OctorateTransactionId";

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

    /// <summary>
    /// Connected account (<c>acct_…</c>) the PaymentIntent was created on (direct charge, BK-02). Refunds and
    /// cancellations of the intent are sent with this <c>Stripe-Account</c>; null for rows recorded before it was
    /// stored (the org's current account is used) and for payments that never went through Stripe.
    /// </summary>
    [MaxLength(255)]
    public string? StripeAccountId { get; set; }

    /// <summary>
    /// True when the PaymentIntent lives on the platform account: its <c>payment_intent.succeeded</c> came through the
    /// platform webhook endpoint without a connected account (BK-04). Refunds are then created without the
    /// <c>Stripe-Account</c> header, never on the org's connected account. False for direct charges
    /// (<see cref="StripeAccountId"/>) and for payments that never went through Stripe.
    /// </summary>
    public bool StripeIntentOnPlatform { get; set; }

    public DateTime? ProcessedAt { get; set; }

    /// <summary>OTA 21% acconto (issue #3). <see cref="Amount"/> remains gross.</summary>
    [Precision(18, 2)]
    public decimal OtaWithholdingTax { get; set; }

    public bool WithholdingTaxApplied { get; set; }

    [Precision(18, 2)]
    public decimal NetAmountAfterWithholding { get; set; }

    public WithholdingSource WithholdingSource { get; set; } = WithholdingSource.None;

    /// <summary>
    /// The <c>transaction</c> this payment was recorded with on its Octorate reservation (E08/F01/US01/T04): the Stripe PaymentIntent id, or
    /// <c>casazen-{paymentId}</c> for a payment the host recorded by hand. Unique in the org (<see cref="OctorateTransactionIndexName"/>), so
    /// a payment is recorded in Octorate once; <c>null</c> while it is not recorded there.
    /// </summary>
    [MaxLength(OctorateTransactionIdMaxLength)]
    public string? OctorateTransactionId { get; set; }

    /// <summary>When Octorate confirmed the payment on the reservation (UTC); <c>null</c> while it is not recorded there.</summary>
    public DateTime? OctorateRegisteredAt { get; set; }

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
    PartiallyRefunded,

    /// <summary>Never collected: the PaymentIntent or SetupIntent was canceled with the booking (BK-02).</summary>
    Canceled
}

public enum PaymentMethod
{
    CreditCard,
    BankTransfer,
    PayPal,
    ApplePay,
    GooglePay,
    CashOnArrival
}
