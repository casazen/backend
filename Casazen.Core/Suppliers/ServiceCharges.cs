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

    public const string PaymentMetadataKey = "serviceRequestPaymentId";

    public const string RequestMetadataKey = "serviceRequestId";

    public const string SupplierMetadataKey = "supplierOrgId";

    public const string Currency = "eur";

    public const string LivePaymentIndexName = "UIX_ServiceRequestPayments_ServiceRequestId_Live";

    /// <summary>Idempotency key of the creation of the <paramref name="count"/>-th PaymentIntent of a payment: <c>service-charge:{id}:{n}</c>.</summary>
    public static string CreationIdempotencyKey(Guid paymentId, int count) => $"service-charge:{paymentId:N}:{count}";

    /// <summary>Idempotency key of the cancellation of a PaymentIntent of a payment.</summary>
    public static string CancellationIdempotencyKey(Guid paymentId, string paymentIntentId) =>
        $"service-charge-cancel:{paymentId:N}:{paymentIntentId}";

    public static bool IsPayable(string? stripeStatus) =>
        stripeStatus is "requires_payment_method" or "requires_confirmation" or "requires_action";

    public const string RefundKind = "service-charge-refund";

    /// <summary><c>metadata</c> key of the refund row (<c>ServiceRequestPaymentRefund.Id</c>): it links a Stripe refund whose answer never came back.</summary>
    public const string RefundMetadataKey = "serviceRequestPaymentRefundId";

    /// <summary>
    /// Idempotency key of the <paramref name="sequence"/>-th refund of a payment: <c>service-charge-refund:{id}:{n}</c>. A retry
    /// of the same refund sends the same key and gets the same Stripe refund.
    /// </summary>
    public static string RefundIdempotencyKey(Guid paymentId, int sequence) => $"service-charge-refund:{paymentId:N}:{sequence}";

    public const string HoldMetadataKey = "showcaseHoldId";

    public const string FundsMetadataKey = "funds";

    public const string HeldFunds = "platform-held";

    /// <summary>
    /// The Stripe <c>transfer_group</c> of the payment of a request created as such (a host who pays from a link): every PaymentIntent,
    /// refund and transfer of the money carries it, so the whole life of the money of one job can be found on the Stripe Dashboard with
    /// one search. One live payment per request, so one group.
    /// </summary>
    public static string TransferGroup(Guid serviceRequestId) => $"service-request-{serviceRequestId:N}";

    public static string HoldTransferGroup(Guid holdId) => $"service-hold-{holdId:N}";

    /// <summary>Idempotency key of the PaymentIntent that blocks the card of a booking hold: the same hold is the same PaymentIntent.</summary>
    public static string HoldAuthorizationIdempotencyKey(Guid holdId) => $"service-hold:{holdId:N}";

    /// <summary>Idempotency key of the cancellation of the block of a hold (the hold lapsed, was removed, or its payment was refused).</summary>
    public static string HoldCancellationIdempotencyKey(Guid holdId, string paymentIntentId) =>
        $"service-hold-cancel:{holdId:N}:{paymentIntentId}";

    /// <summary>Idempotency key of the capture of a payment's PaymentIntent: a payment is charged once, however many times the supplier tries.</summary>
    public static string CaptureIdempotencyKey(Guid paymentId) => $"service-capture:{paymentId:N}";

    /// <summary>
    /// Idempotency key of the <paramref name="attempt"/>-th transfer of a payment to the supplier. The attempt grows only when Stripe
    /// <b>refused</b> the previous one (Stripe remembers the answer to a key for a day, so the same key would replay the refusal); a try
    /// whose answer was lost is repeated with the same key and finds the transfer it may have created.
    /// </summary>
    public static string TransferIdempotencyKey(Guid paymentId, int attempt) => $"service-transfer:{paymentId:N}:{attempt}";

    public const string TransferKind = "service-transfer";

    public static bool IsCapturable(string? stripeStatus) => stripeStatus == "requires_capture";
}

public static class ServicePaymentLimits
{
    public const int OfflineNoteMaxLength = 500;

    public const int MinHoursBetweenRequests = 24;

    public const int TokenMaxLength = 128;

    /// <summary>
    /// The most emails with a payment link a payment gets in all: the request and its reminders (SP-15b, "max 3"). The reminder job
    /// never goes past it, whatever <c>SupplierPayments:ReminderDays</c> lists; what the supplier asks for by hand (once a day) is
    /// not capped by it.
    /// </summary>
    public const int MaxPaymentEmails = 3;

    public const int SyncBatchSize = 100;

    public const int ReminderBatchSize = 500;

    public const int RefundResubmitAfterMinutes = 2;

    public const int AdminMaxPageSize = 100;
}
