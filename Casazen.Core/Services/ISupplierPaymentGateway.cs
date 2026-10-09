namespace Casazen.Core.Services;

/// <summary>
/// The PaymentIntent of a service payment to create (SP-15a): a direct charge on the supplier's connected account.
/// </summary>
/// <param name="ConnectedAccountId">The supplier's Stripe account: the charge is created on it (<c>Stripe-Account</c> header).</param>
/// <param name="AmountCents">What the payer pays.</param>
/// <param name="Currency">Lowercase ISO currency.</param>
/// <param name="ApplicationFeeCents">
/// CasaZen's commission, or <c>null</c> for none. Only a value strictly between zero and <paramref name="AmountCents"/> is sent to
/// Stripe: a zero is never sent as an explicit zero (A3-40) and a fee that is not below the amount is not sent.
/// </param>
/// <param name="Metadata">Ids only (payment, request, supplier); the gateway adds <c>kind = service-charge</c> itself.</param>
/// <param name="IdempotencyKey">A retry with the same key gets the same PaymentIntent.</param>
/// <param name="Description">Plain text shown by Stripe next to the payment; no personal data.</param>
public sealed record ServiceChargeIntentRequest(
    string ConnectedAccountId,
    long AmountCents,
    string Currency,
    long? ApplicationFeeCents,
    IReadOnlyDictionary<string, string> Metadata,
    string IdempotencyKey,
    string? Description);

/// <summary>A PaymentIntent of a service payment as the gateway reports it: only what CasaZen reads, no Stripe type.</summary>
/// <param name="Id">The PaymentIntent id (<c>pi_…</c>).</param>
/// <param name="Status">The Stripe status (<c>requires_payment_method</c>, <c>succeeded</c>, <c>processing</c>, <c>canceled</c>…).</param>
/// <param name="AmountCents">The amount the PaymentIntent asks for.</param>
/// <param name="Currency">Lowercase ISO currency.</param>
/// <param name="ApplicationFeeCents">The application fee on the PaymentIntent; <c>null</c> when it has none.</param>
/// <param name="ClientSecret">What Stripe.js needs to confirm the payment; a credential: never logged and never stored.</param>
/// <param name="ConnectedAccountId">The account the PaymentIntent lives on.</param>
/// <param name="LastErrorCode">Stripe code of the last payment error (<c>card_declined</c>…), if any.</param>
/// <param name="AmountReceivedCents">
/// The amount Stripe actually collected (<c>amount_received</c>), which the sync compares with the price of the payment before it
/// records it as paid (SP-15b). <c>null</c> where it is not known: it is then taken to be <see cref="AmountCents"/>.
/// </param>
public sealed record ServiceChargeIntent(
    string Id,
    string Status,
    long AmountCents,
    string Currency,
    long? ApplicationFeeCents,
    string? ClientSecret,
    string ConnectedAccountId,
    string? LastErrorCode,
    long? AmountReceivedCents = null);

/// <summary>A refund of a service payment to create (SP-15b): a refund of a direct charge, on the supplier's connected account.</summary>
/// <param name="PaymentIntentId">The PaymentIntent that was paid (<c>pi_…</c>).</param>
/// <param name="ConnectedAccountId">The supplier's account the PaymentIntent lives on: the refund is created there (<c>Stripe-Account</c> header) and paid from its balance.</param>
/// <param name="AmountCents">The amount to give back to the payer.</param>
/// <param name="RefundApplicationFee">
/// Give CasaZen's commission back with the refund (<c>refund_application_fee=true</c>): all of it for a refund in full, in
/// proportion for a partial one. True for every payment that was charged a commission; false for one that was not (there is no
/// fee to give back, and Stripe has none to refund).
/// </param>
/// <param name="IdempotencyKey">A retry with the same key gets the same refund (<c>service-charge-refund:{payment}:{n}</c>).</param>
/// <param name="Metadata">Ids only; the gateway adds <c>kind = service-charge-refund</c> itself.</param>
public sealed record ServiceChargeRefundRequest(
    string PaymentIntentId,
    string ConnectedAccountId,
    long AmountCents,
    bool RefundApplicationFee,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A refund as Stripe reports it (the answer of the call, a listing, or a webhook): only what CasaZen reads, no Stripe type.</summary>
/// <param name="Id">The refund (<c>re_…</c>).</param>
/// <param name="PaymentIntentId">The PaymentIntent it refunds; null where Stripe does not say.</param>
/// <param name="AmountCents">The amount refunded.</param>
/// <param name="Status">The Stripe status: <c>pending</c>, <c>requires_action</c>, <c>succeeded</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="FailureReason">Why it failed, when it did; no personal data.</param>
/// <param name="Metadata">What was sent with the request (<c>serviceRequestPaymentRefundId</c>…); empty for a refund made outside CasaZen.</param>
public sealed record ServiceChargeRefund(
    string Id,
    string? PaymentIntentId,
    long AmountCents,
    string? Status,
    string? FailureReason,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// The Stripe calls of the payment of a service request (SP-15a, decision D2: direct charge on the supplier's account with the
/// platform commission as <c>application_fee_amount</c>). It is an interface so that the charge model can change (a
/// destination charge, decision D2 "gateway dietro interfaccia") without touching the service, and so that tests never reach
/// Stripe. <b>This is the only place that sets the application fee</b>: a test of architecture
/// (<c>ApplicationFeeArchitectureTests</c>) fails if any other code does, which keeps the guarantee "no commission on guest
/// bookings and on rent" (<c>StripeServiceApplicationFeeTests</c>, A3-40) true.
/// </summary>
public interface ISupplierPaymentGateway
{
    /// <summary>
    /// Creates the PaymentIntent on the supplier's account with <c>automatic_payment_methods</c>, the commission when it is a real
    /// one, <c>metadata.kind = service-charge</c> and the idempotency key of the request. A Stripe failure is a
    /// <c>StripeException</c> (the API answers 503 <c>payment_provider_error</c>).
    /// </summary>
    Task<ServiceChargeIntent> CreateAsync(ServiceChargeIntentRequest request, CancellationToken cancellationToken = default);

    /// <summary>The PaymentIntent as it is now on Stripe.</summary>
    Task<ServiceChargeIntent> GetAsync(string paymentIntentId, string connectedAccountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a PaymentIntent the payer has not paid and returns it as it is afterwards. A PaymentIntent that cannot be canceled
    /// any more (paid or in progress meanwhile) is returned as it is, not an error: the caller sees its real status.
    /// </summary>
    Task<ServiceChargeIntent> CancelAsync(
        string paymentIntentId,
        string connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a refund of a PaymentIntent on the supplier's account (SP-15b), with <c>refund_application_fee=true</c> when
    /// <see cref="ServiceChargeRefundRequest.RefundApplicationFee"/> is set. A refusal is a <c>StripeException</c>: a 4xx means
    /// Stripe created nothing (the caller records the refund as failed), anything else leaves the outcome unknown (the caller
    /// keeps it pending and resends it with the same key).
    /// </summary>
    Task<ServiceChargeRefund> CreateRefundAsync(ServiceChargeRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every refund Stripe has for a PaymentIntent of the supplier's account.</summary>
    Task<IReadOnlyList<ServiceChargeRefund>> ListRefundsAsync(
        string paymentIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default);
}
