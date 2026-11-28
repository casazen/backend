using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// The payment of a service request made inside CasaZen (SP-15a, decision D2): a <b>direct charge</b> on the supplier's own
/// Stripe Express account with the platform commission as <c>application_fee_amount</c>. CasaZen holds no funds. Created
/// <see cref="ServicePaymentStatus.Requested"/> when the supplier completes a request whose
/// <see cref="ServiceRequest.PaymentMode"/> is <see cref="ServiceRequestPaymentMode.Online"/> (or when the host confirms a final
/// amount above the quote, decision D7); the PaymentIntent is created later, when the payer opens the payment session.
/// An offline payment the supplier records is also a row, <see cref="ServicePaymentChannel.Offline"/> and with no commission.
/// </summary>
/// <remarks>
/// <para><b>Snapshots.</b> <see cref="CommissionPercent"/>, <see cref="ApplicationFeeCents"/> and <see cref="NetCents"/> are
/// computed once, when the row is created, with the percentage in force then (the supplier's own override, else
/// <c>SupplierPayments:CommissionPercent</c>): a later change of the configuration never rewrites a payment. They are what the
/// Stripe webhook of SP-15b compares with what Stripe reports. <see cref="ApplicationFeeCents"/> is the fee that is really sent:
/// <c>0</c> means no fee is sent at all (a fee that rounds to nothing, or that would not be strictly below the amount, is
/// omitted, never sent as an explicit zero: A3-40).</para>
/// <para><b>Tenancy.</b> Two parties and an anonymous payer: <see cref="SupplierOrgId"/> and <see cref="PayerOrgId"/> (null for a
/// private customer). Not <c>ITenantOwned</c>: a host filter would hide the row from the supplier, and the payer reaches it with
/// the link, with no tenant at all. Listed in the allow-list of <c>TenantQueryFilterArchitectureTests</c>; every query carries an
/// explicit predicate (<c>ServiceRequestPaymentTenancyTests</c>).</para>
/// <para><b>One live payment per request.</b> A unique partial index allows a single row per request that is not
/// <see cref="ServicePaymentStatus.Canceled"/>; the creation paths also run under the advisory lock
/// <c>ServiceRequestPayment</c> (key: the request id), so two concurrent sessions create one PaymentIntent.</para>
/// <para><b>Two models of funds (SP-15c-a).</b> <see cref="FundsModel"/> tells which one the row follows.
/// <see cref="ServiceFundsModel.DirectCharge"/> is the one described above. <see cref="ServiceFundsModel.PlatformHeld"/> (decision
/// VT-D1) keeps the same amounts but the money takes another road: the PaymentIntent is on the <b>platform</b> account
/// (<see cref="ConnectedAccountId"/> stays empty, no application fee is sent), the card is blocked then charged
/// (<see cref="ServicePaymentStatus.Authorized"/> → <see cref="ServicePaymentStatus.Held"/>), and CasaZen later transfers
/// <see cref="NetCents"/> to the supplier (<see cref="ServicePaymentStatus.Released"/>) or gives the money back. There,
/// <see cref="ApplicationFeeCents"/> is the commission CasaZen <b>keeps</b> (nothing is sent to Stripe as a fee) and
/// <see cref="PaidAt"/> is when the payer's card was charged, moved to the moment of the transfer when it is released.</para>
/// </remarks>
[Table("ServiceRequestPayments")]
public class ServiceRequestPayment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ServiceRequestId { get; set; }

    public Guid SupplierOrgId { get; set; }

    public ServicePayerKind PayerKind { get; set; } = ServicePayerKind.Host;

    public Guid? PayerOrgId { get; set; }

    public int AmountCents { get; set; }

    [Required, MaxLength(3)]
    public string Currency { get; set; } = ServiceCharges.Currency;

    [Column(TypeName = "numeric(5,2)")]
    public decimal CommissionPercent { get; set; }

    public int ApplicationFeeCents { get; set; }

    public int NetCents { get; set; }

    public bool CommissionAddedToPrice { get; set; }

    /// <summary>
    /// VAT treatment of the commission. Empty until the tax consultant decides (decision D4, <b>[CONSULENTE FISCALE]</b>);
    /// nothing sets it yet.
    /// </summary>
    public ServiceFeeVatMode? FeeVatMode { get; set; }

    public int? FeeVatCents { get; set; }

    public ServicePaymentStatus Status { get; set; } = ServicePaymentStatus.Requested;

    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    [MaxLength(255)]
    public string? ConnectedAccountId { get; set; }

    /// <summary>PaymentIntents created so far: part of the idempotency key of the next one (<c>service-charge:{id}:{n}</c>).</summary>
    public int PaymentIntentCount { get; set; }

    [MaxLength(64)]
    public string? PaymentTokenHash { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? LastSentAt { get; set; }

    public int SentCount { get; set; }

    public DateTime? LateAt { get; set; }

    [MaxLength(100)]
    public string? FailureCode { get; set; }

    public DateTime? PaidAt { get; set; }

    public ServicePaymentChannel? PaidVia { get; set; }

    public int RefundedCents { get; set; }

    [Column(TypeName = "jsonb")]
    public string LineItemsJson { get; set; } = "[]";

    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength)]
    public string? OfflineNote { get; set; }

    [MaxLength(255)]
    public string? MarkedPaidByUserId { get; set; }

    public DateTime? CanceledAt { get; set; }

    public ServiceFundsModel FundsModel { get; set; } = ServiceFundsModel.DirectCharge;

    [MaxLength(255)]
    public string? StripeChargeId { get; set; }

    [MaxLength(100)]
    public string? TransferGroup { get; set; }

    public DateTime? AuthorizedAt { get; set; }

    public DateTime? AuthorizationExpiresAt { get; set; }

    public DateTime? CapturedAt { get; set; }

    /// <summary>
    /// When the money is due to the supplier: the later of the completion of the work and <see cref="CapturedAt"/>, plus
    /// <c>SupplierPayments:ReleaseAfterMinutes</c>. Empty until the supplier completes the work. The release job transfers from here on,
    /// unless a dispute stops it.
    /// </summary>
    public DateTime? ReleaseAt { get; set; }

    public DateTime? ReleaseConfirmedAt { get; set; }

    public DateTime? ReleasedAt { get; set; }

    /// <summary>The Stripe transfer (<c>tr_…</c>) that paid the supplier. At most one per payment (unique index): never two transfers.</summary>
    [MaxLength(255)]
    public string? StripeTransferId { get; set; }

    /// <summary>
    /// Transfers Stripe refused so far. Part of the idempotency key of the next try, so a refusal that Stripe remembers for a day is not
    /// replayed, while a try whose answer was lost is repeated with the same key and finds the same transfer.
    /// </summary>
    public int TransferAttempts { get; set; }

    public DateTime? TransferNextAttemptAt { get; set; }

    [MaxLength(100)]
    public string? TransferFailureCode { get; set; }

    public DateTime? TransferAlertedAt { get; set; }

    public DateTime? HoldWarnedAt { get; set; }

    [MaxLength(255)]
    public string? CardDisputeId { get; set; }

    [MaxLength(50)]
    public string? CardDisputeStatus { get; set; }

    public DateTime? CardDisputeAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ServiceRequestId))]
    public ServiceRequest ServiceRequest { get; set; } = null!;

    [ForeignKey(nameof(SupplierOrgId))]
    public Org SupplierOrg { get; set; } = null!;

    [ForeignKey(nameof(PayerOrgId))]
    public Org? PayerOrg { get; set; }
}
