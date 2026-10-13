using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Real refunds of booking payments on Stripe Connect (BK-02, A3-05, A9-15). Payments of direct bookings are
/// <b>direct charges</b> on the host's connected account, so every refund is created with that account's
/// <c>Stripe-Account</c> header and an idempotency key; the payment becomes Refunded / PartiallyRefunded only when
/// Stripe confirms the refund. Callers authorize the payment first (TN-3): the service never checks roles.
/// </summary>
public interface IPaymentRefundService
{
    /// <summary>
    /// Refunds <see cref="PaymentRefundRequest.Amount"/> (default: everything still refundable) of one payment and
    /// returns the refund as Stripe left it: Succeeded, Pending (Stripe or a retry still working), RequiresAction or
    /// Failed.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The payment does not exist.</exception>
    /// <exception cref="Exceptions.DomainRuleException">Not refundable (state, not paid through Stripe, amount).</exception>
    Task<PaymentRefund> RefundAsync(PaymentRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Refunds of one payment, newest first.</summary>
    Task<IReadOnlyList<PaymentRefund>> GetRefundsAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>Amounts of one payment: refunded (confirmed), in progress and still refundable.</summary>
    Task<PaymentRefundSummary> GetSummaryAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a pending refund to Stripe again with its own idempotency key (retry job after a timeout or a Stripe 5xx).
    /// Does nothing when the refund is no longer pending or Stripe already knows it. Throws on a transient failure so
    /// the job is retried.
    /// </summary>
    Task SubmitPendingAsync(Guid refundId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies refunds reported by a Stripe webhook (<c>refund.*</c>, <c>charge.refund.updated</c>) to the payments of
    /// the PaymentIntent. <paramref name="connectedAccountId"/> is the event's account for the Connect endpoint and null
    /// for the platform endpoint; refunds of a payment of another account are ignored. Returns the refunds that have
    /// just become Succeeded: the caller notifies the guest with <see cref="NotifyGuestAsync"/> after its commit.
    /// </summary>
    Task<IReadOnlyList<Guid>> ApplyStripeRefundsAsync(
        IReadOnlyList<StripeRefundSnapshot> refunds,
        string? connectedAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every refund of the PaymentIntent from Stripe and applies them (<c>charge.refunded</c>, whose charge no
    /// longer embeds its refunds). Same account rule and return value as <see cref="ApplyStripeRefundsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> SyncPaymentIntentRefundsAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues the "refund confirmed" email to the guest of a succeeded refund, once (the claim is atomic). Never throws
    /// for a delivery problem: it is logged.
    /// </summary>
    Task NotifyGuestAsync(Guid refundId, CancellationToken cancellationToken = default);
}

public sealed record PaymentRefundRequest(
    Guid PaymentId,
    decimal? Amount,
    string? Reason,
    string? RequestedByUserId);

/// <param name="PaidAmount">Amount of the payment.</param>
/// <param name="RefundedAmount">Refunds confirmed by Stripe.</param>
/// <param name="PendingRefundAmount">Refunds sent and not confirmed yet (pending, requires action).</param>
/// <param name="RefundableAmount">What can still be refunded.</param>
/// <param name="RefundableOnline">False when the payment did not go through Stripe or is not paid.</param>
public sealed record PaymentRefundSummary(
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal PendingRefundAmount,
    decimal RefundableAmount,
    bool RefundableOnline);

/// <summary>A Stripe refund as reported by a webhook or a list call, without Stripe.net types.</summary>
/// <param name="Status">Stripe status: <c>pending</c>, <c>requires_action</c>, <c>succeeded</c>, <c>failed</c>, <c>canceled</c>.</param>
public sealed record StripeRefundSnapshot(
    string RefundId,
    string? PaymentIntentId,
    long AmountCents,
    string? Status,
    string? FailureReason,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>Schedules the retry of a refund whose submission to Stripe ended with a transient error.</summary>
public interface IPaymentRefundRetryScheduler
{
    void ScheduleSubmit(Guid refundId);
}
