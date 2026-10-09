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
public sealed record ServiceChargeIntent(
    string Id,
    string Status,
    long AmountCents,
    string Currency,
    long? ApplicationFeeCents,
    string? ClientSecret,
    string ConnectedAccountId,
    string? LastErrorCode);

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
}
