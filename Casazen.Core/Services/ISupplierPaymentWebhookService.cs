namespace Casazen.Core.Services;

/// <summary>
/// A <c>payment_intent.*</c> event of a service payment (<c>metadata.kind = service-charge</c>), as the webhook hands it over: only
/// what CasaZen reads, no Stripe type (SP-15b).
/// </summary>
/// <param name="EventId">The Stripe event (<c>evt_…</c>), for the logs.</param>
/// <param name="EventType"><c>payment_intent.succeeded</c>, <c>payment_intent.processing</c>, <c>payment_intent.payment_failed</c> or <c>payment_intent.canceled</c>.</param>
/// <param name="PaymentIntentId">The PaymentIntent (<c>pi_…</c>): the payment is found by it.</param>
/// <param name="AccountId">The connected account the event happened on (the event's <c>account</c>); null for the platform account itself.</param>
/// <param name="PaymentId">The payment id CasaZen wrote in the PaymentIntent's metadata, used only when no payment has the PaymentIntent.</param>
/// <param name="AmountCents">The amount of the PaymentIntent.</param>
/// <param name="AmountReceivedCents">The amount Stripe collected.</param>
/// <param name="Currency">The currency Stripe reports.</param>
/// <param name="ApplicationFeeCents">The application fee on the PaymentIntent; null when it has none.</param>
/// <param name="FailureCode">Stripe's code of the last payment error (<c>card_declined</c>…), if any.</param>
/// <param name="OccurredAt">When Stripe says it happened (the event's creation time); null when unknown. Used to ignore a stale event.</param>
/// <param name="AmountCapturableCents">The amount that can still be charged from a block (<c>amount_capturable</c>), for <c>payment_intent.amount_capturable_updated</c> (SP-15c-a).</param>
/// <param name="LatestChargeId">The charge of the PaymentIntent (<c>ch_…</c>), known once it is charged: the <c>source_transaction</c> of the transfer to the supplier (SP-15c-a).</param>
/// <param name="HoldId">The booking hold the PaymentIntent blocks the card for, from its metadata, before any payment exists (SP-15c-a).</param>
public sealed record ServicePaymentIntentEvent(
    string EventId,
    string EventType,
    string PaymentIntentId,
    string? AccountId,
    Guid? PaymentId,
    long AmountCents,
    long AmountReceivedCents,
    string? Currency,
    long? ApplicationFeeCents,
    string? FailureCode,
    DateTime? OccurredAt,
    long AmountCapturableCents = 0,
    string? LatestChargeId = null,
    Guid? HoldId = null);

/// <summary>A <c>charge.dispute.*</c> event, as the webhook hands it over.</summary>
/// <param name="EventId">The Stripe event (<c>evt_…</c>).</param>
/// <param name="DisputeId">The dispute (<c>dp_…</c>).</param>
/// <param name="PaymentIntentId">The PaymentIntent the disputed charge belongs to; null where Stripe does not say.</param>
/// <param name="AccountId">The connected account the dispute is on.</param>
/// <param name="AmountCents">The amount disputed.</param>
/// <param name="Currency">The currency of the dispute.</param>
/// <param name="Reason">Stripe's reason code (<c>fraudulent</c>, <c>product_not_received</c>…).</param>
/// <param name="Status">Stripe's status of the dispute (<c>needs_response</c>…).</param>
/// <param name="EventType">The Stripe event type (<c>charge.dispute.created</c>, <c>.updated</c>, <c>.closed</c>, <c>.funds_withdrawn</c>, <c>.funds_reinstated</c>); empty for a caller that predates SP-15c-a (it means created).</param>
public sealed record ServiceDisputeEvent(
    string EventId,
    string DisputeId,
    string? PaymentIntentId,
    string? AccountId,
    long AmountCents,
    string? Currency,
    string? Reason,
    string? Status,
    string EventType = "charge.dispute.created");

/// <summary>A <c>transfer.created</c> or <c>transfer.reversed</c> event of the money CasaZen releases to a supplier (SP-15c-a), as the webhook hands it over.</summary>
/// <param name="EventId">The Stripe event (<c>evt_…</c>).</param>
/// <param name="EventType"><c>transfer.created</c> or <c>transfer.reversed</c>.</param>
/// <param name="TransferId">The transfer (<c>tr_…</c>).</param>
/// <param name="AmountCents">The amount transferred.</param>
/// <param name="Currency">The currency of the transfer.</param>
/// <param name="DestinationAccountId">The account that received it.</param>
/// <param name="SourceChargeId">The <c>source_transaction</c> of the transfer, if it has one.</param>
/// <param name="TransferGroup">The transfer group (<c>service-request-{id}</c>).</param>
/// <param name="Reversed">True when the whole transfer was reversed.</param>
/// <param name="PaymentId">The payment id CasaZen wrote in the transfer's metadata; empty for a transfer made outside CasaZen.</param>
public sealed record ServiceTransferEvent(
    string EventId,
    string EventType,
    string TransferId,
    long AmountCents,
    string? Currency,
    string? DestinationAccountId,
    string? SourceChargeId,
    string? TransferGroup,
    bool Reversed,
    Guid? PaymentId);

/// <summary>What happens after the event is committed, never inside its transaction (SP-15b).</summary>
public enum ServicePaymentNoticeKind
{
    /// <summary>The payment was recorded as paid: the receipt to the supplier.</summary>
    Received,

    /// <summary>A payment that was in flight (e.g. a SEPA debit) failed afterwards: the payer gets a new link.</summary>
    FailedInFlight,

    /// <summary>Money arrived that does not match the payment (or for a payment that was withdrawn): the admins are told.</summary>
    NeedsReview,

    /// <summary>The payer disputed the charge: the admins are told.</summary>
    Disputed,

    /// <summary>A refund succeeded: the payer and the supplier are told.</summary>
    Refunded,

    /// <summary>The money CasaZen held was released to the supplier (SP-15c-a): the receipt to the supplier, the same split as a payment received.</summary>
    Released,

    /// <summary>The supplier is owed money but its Stripe account cannot receive it (SP-15c-a): it is asked to complete the account.</summary>
    SupplierAccountNotReady,

    /// <summary>An admin must look at the money of a payment held by CasaZen (SP-15c-a): a transfer that keeps failing, money held for too long, a transfer reversed.</summary>
    FundsAlert,

    /// <summary>A complaint about the job was opened (SP-15c-c): the supplier is asked to reply and the payer gets a receipt.</summary>
    DisputeOpened,

    /// <summary>The supplier replied to a complaint (SP-15c-c): the payer is told it went to CasaZen's team.</summary>
    DisputeReplied,

    /// <summary>A complaint was decided (SP-15c-c): the payer and the supplier are told the outcome.</summary>
    DisputeResolved,

    /// <summary>A complaint was withdrawn by the payer (SP-15c-c): the supplier is told it will be paid.</summary>
    DisputeWithdrawn,

    /// <summary>An admin must look at a complaint (SP-15c-c): it waits for a decision, the target to decide passed, or the refund that was decided did not go through.</summary>
    DisputeAdminAlert,

    /// <summary>
    /// The customer's card was blocked for the offer it accepted on a request on measure (SP-10b-b): the request is taken now. Sends nothing by
    /// itself; the handler of the webhook completes the acceptance (<see cref="IServiceRequestOfferCompletion"/>) once the event is committed.
    /// </summary>
    OfferBlocked,
}

/// <summary>An email to send once the webhook transaction has committed.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="PaymentId">The payment it is about.</param>
/// <param name="RefundId">The refund, for <see cref="ServicePaymentNoticeKind.Refunded"/>.</param>
/// <param name="Detail">A short, plain description for the admins (a mismatch code, a dispute id and reason): ids and codes only, no personal data.</param>
/// <param name="DisputeId">The dispute, for the notices of a complaint (<see cref="ServicePaymentNoticeKind.DisputeOpened"/> and the ones after it).</param>
/// <param name="RequestId">The service request the payment is for, for <see cref="ServicePaymentNoticeKind.OfferBlocked"/>.</param>
public sealed record ServicePaymentNotice(
    ServicePaymentNoticeKind Kind,
    Guid PaymentId,
    Guid? RefundId = null,
    string? Detail = null,
    Guid? DisputeId = null,
    Guid? RequestId = null);

/// <summary>What the service payments did with a refund or dispute event.</summary>
/// <param name="Handled">The PaymentIntent belongs to a service payment. False: it is not one of ours, and the caller treats the event as before (the refunds of the booking payments).</param>
/// <param name="Notices">The emails to send after the commit.</param>
public sealed record ServicePaymentEventResult(bool Handled, IReadOnlyList<ServicePaymentNotice> Notices)
{
    /// <summary>The PaymentIntent is not a service payment's.</summary>
    public static ServicePaymentEventResult NotOurs { get; } = new(false, []);
}

/// <summary>
/// The Stripe webhooks of the service payments (SP-15b, decision D2). The handler (<c>StripeWebhookHandler</c>) routes the events
/// with <c>metadata.kind = service-charge</c> here <b>before</b> its generic <c>payment_intent.succeeded</c> case, inside the event
/// transaction (the claim of the event id and the business updates commit together), and sends the notices after the commit.
/// </summary>
/// <remarks>
/// <para><b>Exactly once, in any order.</b> A duplicate delivery is skipped by the handler (the event claim); events of one payment
/// can arrive in any order (Stripe does not order them and the jobs run in parallel), so the state is derived from what Stripe
/// reports, not from the sequence of the events: a payment that is paid never goes back to a payable state, a stale
/// <c>processing</c> or <c>payment_failed</c> is ignored, and a refund of a payment whose success was not recorded yet reads the
/// PaymentIntent from Stripe first.</para>
/// <para><b>Never "Pagato" on a mismatch.</b> A succeeded PaymentIntent makes the payment and the request paid <b>only if</b> the
/// account, the amount received, the currency, the commission and the PaymentIntent are the ones the payment snapshotted
/// (<c>ServiceChargeVerification</c>); otherwise the payment is <c>NeedsReview</c>, the error is logged and the admins are told.</para>
/// <para>Every method takes the payment lock of the request (<c>ServiceRequestPayment</c>), joining the event transaction, and reads
/// the payment again after it. The processing of these events is <b>not</b> behind the feature flag (money in flight).</para>
/// </remarks>
public interface ISupplierPaymentWebhookService
{
    /// <summary>
    /// A <c>payment_intent.succeeded</c>, <c>.processing</c>, <c>.payment_failed</c> or <c>.canceled</c> of a service payment.
    /// Idempotent: the same event, or a second event that says the same, changes nothing. An event whose PaymentIntent no payment
    /// knows is logged and ignored, except a success, which is money received and goes to review when the payment can be found.
    /// </summary>
    Task<IReadOnlyList<ServicePaymentNotice>> ApplyPaymentIntentEventAsync(
        ServicePaymentIntentEvent paymentEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A <c>charge.refunded</c>: reads the refunds of the PaymentIntent from Stripe and records them, so the payment shows the
    /// real <c>RefundedCents</c> (and <c>PartiallyRefunded</c> / <c>Refunded</c>) whatever the order of the refund events.
    /// <see cref="ServicePaymentEventResult.Handled"/> is false when the PaymentIntent is not a service payment's.
    /// </summary>
    Task<ServicePaymentEventResult> ApplyChargeRefundedAsync(
        string paymentIntentId,
        string? accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A <c>refund.created</c>, <c>.updated</c> or <c>.failed</c>: applies that refund (a refund made outside CasaZen is recorded)
    /// and recomputes the payment. <see cref="ServicePaymentEventResult.Handled"/> is false when its PaymentIntent is not a service
    /// payment's.
    /// </summary>
    Task<ServicePaymentEventResult> ApplyRefundChangedAsync(
        ServiceChargeRefund refund,
        string? accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A <c>charge.dispute.created</c>: logs an error and tells the admins. Nothing else changes (the payment stays as it is; the
    /// admin answers the dispute on Stripe). <see cref="ServicePaymentEventResult.Handled"/> is false when the PaymentIntent is not
    /// a service payment's.
    /// </summary>
    Task<ServicePaymentEventResult> ApplyDisputeCreatedAsync(
        ServiceDisputeEvent dispute,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A <c>charge.dispute.updated</c>, <c>.closed</c>, <c>.funds_withdrawn</c> or <c>.funds_reinstated</c> (SP-15c-a): records the new state
    /// of the card dispute on the payment, for the admin console (a dispute after the release is CasaZen's loss: nothing is recovered
    /// automatically). A payment that does not carry the dispute yet records it. The admins are told when it is a loss
    /// (<c>funds_withdrawn</c>, or <c>closed</c> as lost). <see cref="ServicePaymentEventResult.Handled"/> is false when the PaymentIntent is
    /// not a service payment's.
    /// </summary>
    Task<ServicePaymentEventResult> ApplyDisputeChangedAsync(
        ServiceDisputeEvent dispute,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A <c>transfer.created</c> or <c>transfer.reversed</c> of the money released to a supplier (SP-15c-a). A transfer CasaZen made whose
    /// answer was lost is recorded (the payment is released); a transfer that does not match the payment (amount, account) goes to review; a
    /// reversal is logged and the admins are told, nothing is recovered or paid again automatically. Idempotent. Not ours (no payment knows it)
    /// is not an error.
    /// </summary>
    Task<ServicePaymentEventResult> ApplyTransferEventAsync(
        ServiceTransferEvent transfer,
        CancellationToken cancellationToken = default);

    /// <summary>Sends the emails of the notices, after the webhook transaction committed. A failure is logged: the event is already applied.</summary>
    Task CompleteAsync(IReadOnlyList<ServicePaymentNotice> notices, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues <c>SendPendingPaymentRequestsJob(supplierOrgId)</c> (a supplier just became able to receive payments: the payments
    /// that waited for it can go out). Called after the commit of the <c>account.updated</c> event. A failure to queue is logged and
    /// never thrown: the daily job also sends the payments that are still pending.
    /// </summary>
    void ScheduleSendPendingRequests(Guid supplierOrgId);
}

/// <summary>
/// Queues the one-off job that sends the payment requests of a supplier that has just become ready (SP-15b). An interface so that
/// the services never reference Hangfire (the implementation is in the web project).
/// </summary>
public interface ISupplierPaymentJobScheduler
{
    /// <summary>Enqueues <c>SendPendingPaymentRequestsJob</c> for the supplier org. Does not throw: a failure is logged.</summary>
    void SchedulePendingRequests(Guid supplierOrgId);
}
