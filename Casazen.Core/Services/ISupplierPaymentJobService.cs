using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>What one run of the sync job (<c>service-payment-sync</c>, every 15 minutes) did.</summary>
/// <param name="PaymentsRead">Payments in <c>Processing</c> whose PaymentIntent was read from Stripe.</param>
/// <param name="PaymentsUpdated">Of those, the ones whose state changed (paid, failed, canceled, to review).</param>
/// <param name="RefundsRead">Refunds waiting for Stripe's answer that were resent or read.</param>
/// <param name="RefundsUpdated">Of those, the ones whose state changed.</param>
/// <param name="Errors">Items that could not be read or applied; the next run retries them.</param>
public sealed record ServicePaymentSyncRun(int PaymentsRead, int PaymentsUpdated, int RefundsRead, int RefundsUpdated, int Errors);

/// <summary>What one run of the reminder job (<c>service-payment-reminders</c>, daily 07:30 UTC) did.</summary>
/// <param name="EmailsEnabled">False when the feature flag <c>SupplierOnlinePayments</c> is off: the payments are still flagged late, but nothing is sent.</param>
/// <param name="MarkedLate">Payments newly flagged as late.</param>
/// <param name="RequestsSent">Pending payments whose first request went out (the supplier became ready, or the payer got an address).</param>
/// <param name="RemindersSent">Reminders sent.</param>
/// <param name="Skipped">Candidates that were no longer due when the job looked at them under the lock.</param>
/// <param name="Errors">Payments that could not be handled; the next run retries them.</param>
public sealed record ServicePaymentReminderRun(
    bool EmailsEnabled,
    int MarkedLate,
    int RequestsSent,
    int RemindersSent,
    int Skipped,
    int Errors);

/// <summary>
/// The jobs of the service payments (SP-15b): <c>service-payment-sync</c>, <c>service-payment-reminders</c> and
/// <c>SendPendingPaymentRequestsJob(orgId)</c>. Each payment is handled under the payment lock of its request, in its own
/// transaction, and read again after the lock, so a run that overlaps another (or a webhook, a session, the supplier's request)
/// never sends a link twice or records a payment twice. An error on one payment is logged and never stops the others.
/// </summary>
/// <remarks>
/// <para>The flag <c>SupplierOnlinePayments</c> stops what <b>creates</b> a payment request: the first requests and the reminders.
/// The sync (money in flight) and the late flag are not behind it.</para>
/// </remarks>
public interface ISupplierPaymentJobService
{
    /// <summary>
    /// Reads from Stripe the PaymentIntents of the payments in <c>Processing</c> (oldest first, at most
    /// <c>ServicePaymentLimits.SyncBatchSize</c>) and applies what they say exactly as the webhook would (paid after the checks,
    /// failed, canceled, to review): a safety net for an event that was lost. It also resends the refunds that never got Stripe's
    /// answer and refreshes the ones Stripe has not completed.
    /// </summary>
    Task<ServicePaymentSyncRun> SynchronizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily run: flags as late the payments asked for <c>LateAfterDays</c> ago and still unpaid, sends the first request of
    /// the payments that were pending, and sends the reminders that are due (at +2 and +7 days from the first request, at most
    /// three emails with a link in all, never twice in a day).
    /// </summary>
    Task<ServicePaymentReminderRun> RunRemindersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the payment requests that waited for the supplier (its Stripe account could not take payments yet, or the payer had
    /// no address): queued when <c>account.updated</c> says the supplier can receive payments. Returns how many went out. With the
    /// flag off nothing is sent.
    /// </summary>
    Task<int> SendPendingRequestsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Refunds of the service payments by a platform admin (SP-15b, decisions D2 and D3). The payment is a direct charge on the
/// supplier's account, so the refund is created there and paid from the supplier's balance; it is sent with
/// <c>refund_application_fee=true</c> (when the payment carried a commission), which gives CasaZen's commission back in full for a
/// refund in full and in proportion for a partial one.
/// </summary>
/// <remarks>
/// <para><b>Exactly once.</b> The refund is written <c>Pending</c> under the payment lock before Stripe is called, with the
/// idempotency key <c>service-charge-refund:{payment}:{n}</c>; the amounts reserved by pending refunds cannot be refunded again;
/// a timeout keeps the refund pending and the sync job resends it with the same key (after looking for it on Stripe first).</para>
/// <para><b>Honest status.</b> The payment becomes <c>PartiallyRefunded</c> or <c>Refunded</c>, and <c>RefundedCents</c> grows,
/// only from refunds that Stripe reports succeeded. The request stays <c>Pagato</c>.</para>
/// </remarks>
public interface ISupplierPaymentRefundService
{
    /// <summary>
    /// Refunds <paramref name="amountCents"/> of the payment (everything still refundable when null). 404
    /// <c>service_payment_not_found</c>; 422 <c>service_payment_not_refundable</c> (not paid online),
    /// <c>service_payment_refund_offline</c> (recorded as received outside CasaZen), <c>service_payment_nothing_to_refund</c>,
    /// <c>service_payment_refund_amount_invalid</c> and <c>service_payment_refund_amount_exceeds</c>. A refund Stripe refuses (4xx)
    /// is returned <c>Failed</c> with its code, one whose outcome is unknown is returned <c>Pending</c>.
    /// </summary>
    Task<ServiceRequestPaymentRefund> RefundAsync(
        Guid paymentId,
        int? amountCents,
        string? reason,
        string actorUserId,
        CancellationToken cancellationToken = default);
}
