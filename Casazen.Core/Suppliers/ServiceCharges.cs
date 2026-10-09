namespace Casazen.Core.Suppliers;

/// <summary>
/// Stripe markers of the payment of a service request (SP-15a): the metadata that routes the webhooks, the currency and the
/// idempotency keys. Runbook: <c>docs/runbooks/stripe.md</c> § "Services of the suppliers (SP-15)".
/// </summary>
public static class ServiceCharges
{
    /// <summary>
    /// <c>metadata.kind</c> of the PaymentIntents of a service payment. The Stripe webhook of SP-15b routes on it, <b>before</b>
    /// the generic <c>payment_intent.succeeded</c> case (otherwise the event would be "processed" with no effect, or handed to
    /// the booking settlement).
    /// </summary>
    public const string Kind = "service-charge";

    /// <summary><c>metadata</c> key of the payment id.</summary>
    public const string PaymentMetadataKey = "serviceRequestPaymentId";

    /// <summary><c>metadata</c> key of the service request id.</summary>
    public const string RequestMetadataKey = "serviceRequestId";

    /// <summary><c>metadata</c> key of the supplier org id.</summary>
    public const string SupplierMetadataKey = "supplierOrgId";

    public const string Currency = "eur";

    /// <summary>
    /// Name of the unique partial index "one payment per request that is not canceled" (<c>ServiceRequestPayments</c>). A save that
    /// violates it means another call created the payment first: a conflict (409), never a 500.
    /// </summary>
    public const string LivePaymentIndexName = "UIX_ServiceRequestPayments_ServiceRequestId_Live";

    /// <summary>Idempotency key of the creation of the <paramref name="count"/>-th PaymentIntent of a payment: <c>service-charge:{id}:{n}</c>.</summary>
    public static string CreationIdempotencyKey(Guid paymentId, int count) => $"service-charge:{paymentId:N}:{count}";

    /// <summary>Idempotency key of the cancellation of a PaymentIntent of a payment.</summary>
    public static string CancellationIdempotencyKey(Guid paymentId, string paymentIntentId) =>
        $"service-charge-cancel:{paymentId:N}:{paymentIntentId}";

    /// <summary>The PaymentIntent statuses the payer can still pay: nothing was collected.</summary>
    public static bool IsPayable(string? stripeStatus) =>
        stripeStatus is "requires_payment_method" or "requires_confirmation" or "requires_action";
}

/// <summary>
/// Technical bounds of the payment of a service request (SP-15a). Like the other limits of the supplier area they keep a row
/// small and a typo harmless: they are not product rules.
/// </summary>
public static class ServicePaymentLimits
{
    /// <summary>Longest note the supplier leaves when it records a payment received outside CasaZen (same size as the other reasons).</summary>
    public const int OfflineNoteMaxLength = 500;

    /// <summary>The supplier may send (or remind) the payment request once in this many hours.</summary>
    public const int MinHoursBetweenRequests = 24;

    /// <summary>Longest token of a payment link accepted by the public endpoints (the real one is 43 characters).</summary>
    public const int TokenMaxLength = 128;
}
