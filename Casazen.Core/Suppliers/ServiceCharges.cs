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

    /// <summary><c>metadata.kind</c> of the Stripe refunds CasaZen creates for a service payment (SP-15b).</summary>
    public const string RefundKind = "service-charge-refund";

    /// <summary><c>metadata</c> key of the refund row (<c>ServiceRequestPaymentRefund.Id</c>): it links a Stripe refund whose answer never came back.</summary>
    public const string RefundMetadataKey = "serviceRequestPaymentRefundId";

    /// <summary>
    /// Idempotency key of the <paramref name="sequence"/>-th refund of a payment: <c>service-charge-refund:{id}:{n}</c>. A retry
    /// of the same refund sends the same key and gets the same Stripe refund.
    /// </summary>
    public static string RefundIdempotencyKey(Guid paymentId, int sequence) => $"service-charge-refund:{paymentId:N}:{sequence}";

    // ─── The payment held by CasaZen (SP-15c-a) ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>metadata</c> key of the booking hold a PaymentIntent blocks the card for, before the request exists (the payment is created when
    /// the e-mail is checked and the block succeeded). The webhook finds the hold by the PaymentIntent and checks this id against it.
    /// </summary>
    public const string HoldMetadataKey = "showcaseHoldId";

    /// <summary>
    /// <c>metadata</c> key that marks a PaymentIntent created on the platform account for money CasaZen holds (<see cref="HeldFunds"/>), as
    /// opposed to a direct charge on a supplier's account. Informative: the payment's <c>FundsModel</c> decides what CasaZen does.
    /// </summary>
    public const string FundsMetadataKey = "funds";

    /// <summary>Value of <see cref="FundsMetadataKey"/> for a payment held by the platform.</summary>
    public const string HeldFunds = "platform-held";

    /// <summary>
    /// The Stripe <c>transfer_group</c> of the payment of a request created as such (a host who pays from a link): every PaymentIntent,
    /// refund and transfer of the money carries it, so the whole life of the money of one job can be found on the Stripe Dashboard with
    /// one search. One live payment per request, so one group.
    /// </summary>
    public static string TransferGroup(Guid serviceRequestId) => $"service-request-{serviceRequestId:N}";

    /// <summary>
    /// The <c>transfer_group</c> of the money of a booking hold, which has a PaymentIntent before the request exists (the request id is not
    /// known yet). The payment keeps it (<c>ServiceRequestPayment.TransferGroup</c>), so the transfer to the supplier is found by it.
    /// </summary>
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

    /// <summary><c>metadata.kind</c> of the Stripe transfers CasaZen makes to a supplier for a service payment.</summary>
    public const string TransferKind = "service-transfer";

    /// <summary>The PaymentIntent statuses of a block that can be charged: the card is blocked and nothing was collected yet.</summary>
    public static bool IsCapturable(string? stripeStatus) => stripeStatus == "requires_capture";
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

    /// <summary>
    /// The most emails with a payment link a payment gets in all: the request and its reminders (SP-15b, "max 3"). The reminder job
    /// never goes past it, whatever <c>SupplierPayments:ReminderDays</c> lists; what the supplier asks for by hand (once a day) is
    /// not capped by it.
    /// </summary>
    public const int MaxPaymentEmails = 3;

    /// <summary>How many payments or refunds one run of the sync job (every 15 minutes) reads from Stripe: the oldest first, the rest on the next run.</summary>
    public const int SyncBatchSize = 100;

    /// <summary>How many payments one run of the daily reminder job handles: the oldest first, the rest on the next run.</summary>
    public const int ReminderBatchSize = 500;

    /// <summary>
    /// A refund still waiting for Stripe's answer is resent by the sync job only after this many minutes: the request that wrote
    /// it is probably still running.
    /// </summary>
    public const int RefundResubmitAfterMinutes = 2;

    /// <summary>Longest list of payments the admin gets in one page.</summary>
    public const int AdminMaxPageSize = 100;
}
