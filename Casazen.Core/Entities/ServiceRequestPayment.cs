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
/// </remarks>
[Table("ServiceRequestPayments")]
public class ServiceRequestPayment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The request being paid. A request is never deleted while a payment points to it (restrict).</summary>
    public Guid ServiceRequestId { get; set; }

    /// <summary>The supplier org that is paid (the org of the Stripe account the charge lands on).</summary>
    public Guid SupplierOrgId { get; set; }

    public ServicePayerKind PayerKind { get; set; } = ServicePayerKind.Host;

    /// <summary>The host org that pays; null for a private customer.</summary>
    public Guid? PayerOrgId { get; set; }

    /// <summary>The price the payer pays, in cents: the final amount of the request, commission included (the commission comes out of what the supplier receives).</summary>
    public int AmountCents { get; set; }

    /// <summary>Lowercase ISO currency (<c>eur</c>).</summary>
    [Required, MaxLength(3)]
    public string Currency { get; set; } = ServiceCharges.Currency;

    /// <summary>The commission percentage in force when the row was created (snapshot).</summary>
    [Column(TypeName = "numeric(5,2)")]
    public decimal CommissionPercent { get; set; }

    /// <summary>The commission in cents that is sent to Stripe as <c>application_fee_amount</c>; 0 when none is sent (snapshot).</summary>
    public int ApplicationFeeCents { get; set; }

    /// <summary><see cref="AmountCents"/> minus <see cref="ApplicationFeeCents"/>: what the supplier receives before Stripe's own fees (snapshot).</summary>
    public int NetCents { get; set; }

    /// <summary>
    /// VAT treatment of the commission. Empty until the tax consultant decides (decision D4, <b>[CONSULENTE FISCALE]</b>);
    /// nothing sets it yet.
    /// </summary>
    public ServiceFeeVatMode? FeeVatMode { get; set; }

    /// <summary>VAT on the commission, in cents; empty together with <see cref="FeeVatMode"/>.</summary>
    public int? FeeVatCents { get; set; }

    public ServicePaymentStatus Status { get; set; } = ServicePaymentStatus.Requested;

    /// <summary>The current PaymentIntent on <see cref="ConnectedAccountId"/>; null until the payer opens a payment session.</summary>
    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    /// <summary>
    /// The supplier's Stripe account the current PaymentIntent was created on (snapshot). The webhook accepts a payment only if it
    /// comes from this account.
    /// </summary>
    [MaxLength(255)]
    public string? ConnectedAccountId { get; set; }

    /// <summary>PaymentIntents created so far: part of the idempotency key of the next one (<c>service-charge:{id}:{n}</c>).</summary>
    public int PaymentIntentCount { get; set; }

    /// <summary>SHA-256 of the token of the payment link (<see cref="Services.CheckoutOutcomes.HashToken"/>); the raw token is only in the email. Null while no link was sent.</summary>
    [MaxLength(64)]
    public string? PaymentTokenHash { get; set; }

    /// <summary>When the payment was first asked of the payer (the first email). Null while the request is pending (the supplier cannot be paid yet).</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>When a link was last emailed (request or reminder): the validity of the current link counts from here, and the supplier may ask again once a day.</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>Emails with a link sent so far (the request and its reminders).</summary>
    public int SentCount { get; set; }

    /// <summary>When the payment was flagged as late (SP-15b): <c>LateAfterDays</c> days after <see cref="RequestedAt"/>. Null while on time.</summary>
    public DateTime? LateAt { get; set; }

    /// <summary>Stripe error code of the last failed attempt (e.g. <c>card_declined</c>); no personal data.</summary>
    [MaxLength(100)]
    public string? FailureCode { get; set; }

    public DateTime? PaidAt { get; set; }

    public ServicePaymentChannel? PaidVia { get; set; }

    /// <summary>Cents given back to the payer so far (SP-15b); never above <see cref="AmountCents"/>.</summary>
    public int RefundedCents { get; set; }

    /// <summary>JSON array of <see cref="ServiceRequestPriceLine"/> (<c>kind</c>, <c>label</c>, <c>amountCents</c>): the price of the work as it was when the payment was created.</summary>
    [Column(TypeName = "jsonb")]
    public string LineItemsJson { get; set; } = "[]";

    /// <summary>
    /// What the supplier wrote when it recorded the payment as received outside CasaZen: for a request paid inside CasaZen it is
    /// the reason of the exception (decision D5), required and kept as its trace.
    /// </summary>
    [MaxLength(ServicePaymentLimits.OfflineNoteMaxLength)]
    public string? OfflineNote { get; set; }

    /// <summary>The user who recorded the offline payment.</summary>
    [MaxLength(255)]
    public string? MarkedPaidByUserId { get; set; }

    /// <summary>When the payment was dropped (<see cref="ServicePaymentStatus.Canceled"/>).</summary>
    public DateTime? CanceledAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ServiceRequestId))]
    public ServiceRequest ServiceRequest { get; set; } = null!;

    [ForeignKey(nameof(SupplierOrgId))]
    public Org SupplierOrg { get; set; } = null!;

    [ForeignKey(nameof(PayerOrgId))]
    public Org? PayerOrg { get; set; }
}
