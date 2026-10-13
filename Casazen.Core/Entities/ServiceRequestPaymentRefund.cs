using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// One refund of a <see cref="ServiceRequestPayment"/> made on Stripe (SP-15b, decision D2). The payment is a direct charge on the
/// supplier's account, so the refund is created there and paid from the supplier's balance; it is sent with
/// <c>refund_application_fee=true</c>, which gives CasaZen's commission back in proportion to the amount refunded.
/// </summary>
/// <remarks>
/// <para><b>Exactly once.</b> The row is written <see cref="ServicePaymentRefundStatus.Pending"/> before Stripe is called, under the
/// payment lock, and its <see cref="Sequence"/> is the number of the idempotency key
/// (<c>service-charge-refund:{payment}:{n}</c>, <see cref="IdempotencyKey"/>): a timeout or a retry sends the same key and gets the
/// same refund. Pending refunds count as reserved, so the same euros are never refunded twice.</para>
/// <para><b>Status.</b> Only Stripe moves a refund to <see cref="ServicePaymentRefundStatus.Succeeded"/> (the answer of the call, or
/// the <c>charge.refunded</c> / <c>refund.*</c> webhooks, in any order and any number of times: applying one is idempotent), and the
/// payment's <see cref="ServiceRequestPayment.RefundedCents"/> is always the sum of the succeeded ones. A refund made outside
/// CasaZen is recorded with <see cref="ServicePaymentRefundOrigin.Stripe"/>.</para>
/// <para><b>Tenancy.</b> Like its payment it has two parties, so it is not <c>ITenantOwned</c>: it belongs to the payment, and
/// only the payments service and the admin reads touch it, always through the payment (allow-list of
/// <c>TenantQueryFilterArchitectureTests</c>, guard <c>ServiceRequestPaymentRefundTenancyTests</c>).</para>
/// </remarks>
[Table("ServiceRequestPaymentRefunds")]
public class ServiceRequestPaymentRefund
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The payment that is refunded. A payment with refunds is never deleted (restrict).</summary>
    public Guid ServiceRequestPaymentId { get; set; }

    /// <summary>1, 2, 3… within the payment, in the order the refunds were recorded: the <c>n</c> of the idempotency key.</summary>
    public int Sequence { get; set; }

    /// <summary>The amount refunded to the payer, in cents of euro (what Stripe reports once it knows it).</summary>
    public int AmountCents { get; set; }

    public ServicePaymentRefundStatus Status { get; set; } = ServicePaymentRefundStatus.Pending;

    public ServicePaymentRefundOrigin Origin { get; set; } = ServicePaymentRefundOrigin.Admin;

    /// <summary>The Stripe refund (<c>re_…</c>), known once Stripe answered or a webhook linked it. Unique.</summary>
    [MaxLength(255)]
    public string? StripeRefundId { get; set; }

    /// <summary>
    /// Idempotency key sent to Stripe with the creation (<c>service-charge-refund:{payment}:{n}</c>, see
    /// <see cref="ServiceCharges.RefundIdempotencyKey"/>). Null for a refund made outside CasaZen. Unique.
    /// </summary>
    [MaxLength(255)]
    public string? IdempotencyKey { get; set; }

    /// <summary>The Stripe error code or the failure reason when the refund failed or was canceled; no personal data.</summary>
    [MaxLength(100)]
    public string? FailureCode { get; set; }

    /// <summary>The admin's note (why it was refunded): at most 500 characters, internal, never sent to Stripe.</summary>
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength)]
    public string? Reason { get; set; }

    /// <summary>The Auth0 subject of the admin who asked for the refund; null for a refund made outside CasaZen.</summary>
    [MaxLength(255)]
    public string? RequestedByUserId { get; set; }

    /// <summary>
    /// The part of CasaZen's commission that goes back with this refund, in cents: the commission refunded in proportion to the
    /// total refunded so far (<see cref="SupplierCommission.FeeRefundedCents"/>), rounded to the cent. Empty until the refund
    /// succeeds; recomputed from all the succeeded refunds, so the parts always add up to the proportional figure. The Stripe
    /// Dashboard is the authority on the exact amount Stripe gave back.
    /// </summary>
    public int? ApplicationFeeRefundedCents { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the refund became <see cref="ServicePaymentRefundStatus.Succeeded"/>.</summary>
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(ServiceRequestPaymentId))]
    public ServiceRequestPayment ServiceRequestPayment { get; set; } = null!;
}
