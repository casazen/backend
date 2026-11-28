using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The Stripe webhooks of the service payments (SP-15b): <c>payment_intent.*</c> with <c>metadata.kind = service-charge</c>, applied
/// exactly once and in any order, and the dispute notice. The refund events are in <c>SupplierPaymentService.Refunds.cs</c>.
/// </summary>
/// <remarks>
/// <para><b>The state is derived from what Stripe reports, not from the sequence of the events.</b> Every event (and every
/// PaymentIntent the sync job reads) is turned into one observation of the PaymentIntent (<see cref="ObservedIntent"/>), and
/// <see cref="ApplyObservationAsync"/> is the only place that moves a payment from it. So the same observation applied twice changes
/// nothing, a payment that is paid never goes back to a payable state, and an event that is older than the last change is ignored
/// where it could otherwise undo a newer one.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    private enum ObservedState
    {
        Open,
        Succeeded,
        Processing,
        Failed,
        Canceled,
    }

    private sealed record ObservedIntent(
        string PaymentIntentId,
        ObservedState State,
        string? AccountId,
        long AmountCents,
        long AmountReceivedCents,
        string? Currency,
        long? ApplicationFeeCents,
        string? FailureCode,
        DateTime? OccurredAt,
        string Source);

    private sealed record PaymentRef(Guid Id, Guid ServiceRequestId);

    public async Task<IReadOnlyList<ServicePaymentNotice>> ApplyPaymentIntentEventAsync(
        ServicePaymentIntentEvent paymentEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);

        var found = await FindPaymentRefAsync(paymentEvent.PaymentIntentId, paymentEvent.PaymentId, cancellationToken);
        if (found is null)
        {
            // Not ours to act on: no payment has this PaymentIntent. Never an error (Stripe would retry it forever).
            logger.LogWarning(
                "Stripe event {EventId} ({EventType}) for service PaymentIntent {PaymentIntentId} (account {AccountId}): no service payment has it, ignored",
                paymentEvent.EventId, paymentEvent.EventType, paymentEvent.PaymentIntentId, paymentEvent.AccountId ?? "none");
            return [];
        }

        var observed = Observe(paymentEvent);
        var notice = await UnderPaymentLockAsync(
            found.ServiceRequestId,
            async () =>
            {
                var payment = await LoadPaymentAsync(found.Id, cancellationToken);
                if (payment is null)
                    return null;

                return payment.FundsModel == ServiceFundsModel.PlatformHeld
                    ? ApplyHeldIntentEvent(payment, paymentEvent)
                    : await ApplyObservationAsync(payment, observed, cancellationToken);
            },
            cancellationToken);

        return notice is null ? [] : [notice];
    }

    private async Task<PaymentRef?> FindPaymentRefAsync(string paymentIntentId, Guid? paymentId, CancellationToken cancellationToken)
    {
        var byIntent = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.StripePaymentIntentId == paymentIntentId)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .FirstOrDefaultAsync(cancellationToken);
        if (byIntent is not null || paymentId is not { } id)
            return byIntent;

        // A PaymentIntent that names a payment but is not recorded on it: found so that a success is reviewed, never confirmed.
        return await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ServiceRequestPayment?> LoadPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await db.ServiceRequestPayments.Where(p => p.Id == paymentId).FirstOrDefaultAsync(cancellationToken);
        if (payment is not null)
            await db.Entry(payment).ReloadAsync(cancellationToken);

        return payment;
    }

    private async Task<T> UnderPaymentLockAsync<T>(Guid requestId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await LockAsync(requestId, cancellationToken);
        var result = await work();
        await SaveAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return result;
    }

    private static ObservedIntent Observe(ServicePaymentIntentEvent e) => new(
        e.PaymentIntentId,
        e.EventType switch
        {
            "payment_intent.succeeded" => ObservedState.Succeeded,
            "payment_intent.processing" => ObservedState.Processing,
            "payment_intent.payment_failed" => ObservedState.Failed,
            "payment_intent.canceled" => ObservedState.Canceled,
            _ => ObservedState.Open,
        },
        e.AccountId,
        e.AmountCents,
        e.AmountReceivedCents,
        e.Currency,
        e.ApplicationFeeCents,
        e.FailureCode,
        e.OccurredAt,
        e.EventId);

    private static ObservedIntent Observe(ServiceChargeIntent intent, ServiceRequestPayment payment) => new(
        intent.Id,
        intent.Status switch
        {
            "succeeded" => ObservedState.Succeeded,
            "processing" => ObservedState.Processing,
            "canceled" => ObservedState.Canceled,
            "requires_payment_method" when intent.LastErrorCode is not null || payment.Status == ServicePaymentStatus.Processing => ObservedState.Failed,
            _ => ObservedState.Open,
        },
        intent.ConnectedAccountId,
        intent.AmountCents,
        intent.AmountReceivedCents ?? intent.AmountCents,
        intent.Currency,
        intent.ApplicationFeeCents,
        intent.LastErrorCode,
        OccurredAt: null,
        Source: "sync");

    /// <summary>
    /// Applies one observation of a PaymentIntent to its payment (the payment is locked and read again). Returns the notice to send
    /// after the commit, if the observation made one necessary. Idempotent: applying it again changes nothing.
    /// </summary>
    private async Task<ServicePaymentNotice?> ApplyObservationAsync(
        ServiceRequestPayment payment,
        ObservedIntent observed,
        CancellationToken cancellationToken)
    {
        var now = Now();
        var isCurrent = string.Equals(payment.StripePaymentIntentId, observed.PaymentIntentId, StringComparison.Ordinal);

        switch (observed.State)
        {
            case ObservedState.Succeeded:
                return await ApplySucceededAsync(payment, observed, isCurrent, now, cancellationToken);

            case ObservedState.Processing:
                if (!isCurrent || payment.Status is not (ServicePaymentStatus.Requested or ServicePaymentStatus.Failed))
                    return null;

                // A "processing" that is older than the failure the payment shows is the stale half of a retry: ignore it.
                if (payment.Status == ServicePaymentStatus.Failed && IsOlderThanLastChange(observed, payment))
                {
                    LogStale(payment, observed);
                    return null;
                }

                payment.Status = ServicePaymentStatus.Processing;
                payment.UpdatedAt = now;
                return null;

            case ObservedState.Failed:
                if (!isCurrent || payment.Status is not (ServicePaymentStatus.Requested or ServicePaymentStatus.Processing or ServicePaymentStatus.Failed))
                    return null;

                // A failure that is older than the processing the payment shows is a previous attempt: the payer retried since.
                if (payment.Status == ServicePaymentStatus.Processing && IsOlderThanLastChange(observed, payment))
                {
                    LogStale(payment, observed);
                    return null;
                }

                var wasInFlight = payment.Status == ServicePaymentStatus.Processing;
                payment.Status = ServicePaymentStatus.Failed;
                payment.FailureCode = Truncate(observed.FailureCode, 100);
                payment.UpdatedAt = now;

                return wasInFlight ? new ServicePaymentNotice(ServicePaymentNoticeKind.FailedInFlight, payment.Id) : null;

            case ObservedState.Canceled:
                if (!isCurrent || payment.Status is not (ServicePaymentStatus.Requested or ServicePaymentStatus.Failed or ServicePaymentStatus.Processing))
                    return null;

                payment.StripePaymentIntentId = null;
                payment.Status = payment.Status == ServicePaymentStatus.Failed ? ServicePaymentStatus.Failed : ServicePaymentStatus.Requested;
                payment.UpdatedAt = now;
                return null;

            default:
                return null;
        }
    }

    private async Task<ServicePaymentNotice?> ApplySucceededAsync(
        ServiceRequestPayment payment,
        ObservedIntent observed,
        bool isCurrent,
        DateTime now,
        CancellationToken cancellationToken)
    {
        switch (payment.Status)
        {
            case ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded:
                if (!isCurrent)
                {
                    logger.LogError(
                        "Service payment {PaymentId} is already paid ({PaidVia}) but PaymentIntent {PaymentIntentId} succeeded too (event {EventId}): refund one of the two charges",
                        payment.Id, payment.PaidVia, observed.PaymentIntentId, observed.Source);
                }

                return null;

            case ServicePaymentStatus.Canceled:
                logger.LogError(
                    "Service payment {PaymentId} was withdrawn but PaymentIntent {PaymentIntentId} succeeded on {AccountId} (event {EventId}): the payer was charged for work that is paid already, refund it",
                    payment.Id, observed.PaymentIntentId, observed.AccountId ?? "none", observed.Source);
                return new ServicePaymentNotice(ServicePaymentNoticeKind.NeedsReview, payment.Id, Detail: "payment_withdrawn");

            case ServicePaymentStatus.NeedsReview:
                logger.LogError(
                    "Service payment {PaymentId} already needs a review and PaymentIntent {PaymentIntentId} succeeded again (event {EventId})",
                    payment.Id, observed.PaymentIntentId, observed.Source);
                return null;
        }

        var mismatches = ServiceChargeVerification.Mismatches(
            new ServiceChargeExpectation(
                payment.ConnectedAccountId, payment.StripePaymentIntentId, payment.AmountCents, payment.Currency, payment.ApplicationFeeCents),
            new ServiceChargeObservation(
                observed.PaymentIntentId, observed.AccountId, observed.AmountCents, observed.AmountReceivedCents, observed.Currency, observed.ApplicationFeeCents));

        if (mismatches.Count > 0)
        {
            var code = ServiceChargeVerification.ReviewCode(mismatches);
            payment.Status = ServicePaymentStatus.NeedsReview;
            payment.FailureCode = Truncate(code, 100);
            payment.UpdatedAt = now;
            logger.LogError(
                "Service payment {PaymentId} NOT recorded as paid: PaymentIntent {PaymentIntentId} (event {EventId}) differs from what was asked for ({Mismatches}). "
                + "Received on {ReceivedAccount}: {ReceivedAmount}/{ReceivedAmountReceived} {ReceivedCurrency}, fee {ReceivedFee}; expected on {ExpectedAccount} (PaymentIntent {ExpectedIntent}): {ExpectedAmount} {ExpectedCurrency}, fee {ExpectedFee}",
                payment.Id, observed.PaymentIntentId, observed.Source, code,
                observed.AccountId ?? "none", observed.AmountCents, observed.AmountReceivedCents, observed.Currency ?? "none", observed.ApplicationFeeCents?.ToString() ?? "none",
                payment.ConnectedAccountId ?? "none", payment.StripePaymentIntentId ?? "none", payment.AmountCents, payment.Currency, payment.ApplicationFeeCents);
            return new ServicePaymentNotice(ServicePaymentNoticeKind.NeedsReview, payment.Id, Detail: code);
        }

        var paidAt = observed.OccurredAt is { } at && at < now ? at : now;
        payment.Status = ServicePaymentStatus.Paid;
        payment.PaidAt = paidAt;
        payment.PaidVia = ServicePaymentChannel.Stripe;
        payment.FailureCode = null;
        payment.UpdatedAt = now;
        await MarkRequestPaidAsync(payment, paidAt, now, cancellationToken);

        logger.LogInformation(
            "Service payment {PaymentId} of request {RequestId} paid through PaymentIntent {PaymentIntentId} (event {EventId})",
            payment.Id, payment.ServiceRequestId, observed.PaymentIntentId, observed.Source);
        return new ServicePaymentNotice(ServicePaymentNoticeKind.Received, payment.Id);
    }

    private async Task MarkRequestPaidAsync(
        ServiceRequestPayment payment,
        DateTime paidAt,
        DateTime now,
        CancellationToken cancellationToken,
        ServiceRequestActorParty paidBy = ServiceRequestActorParty.Host)
    {
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.Id == payment.ServiceRequestId)
            .FirstOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            logger.LogError("Service payment {PaymentId} was paid but its request {RequestId} does not exist", payment.Id, payment.ServiceRequestId);
            return;
        }

        if (request.Status == ServiceRequestStatus.Completato)
        {
            request.Status = ServiceRequestStatus.Pagato;
            request.PaidAt = paidAt;
            request.PaidBy = paidBy;
            request.UpdatedAt = now;
        }
        else if (request.Status != ServiceRequestStatus.Pagato)
        {
            logger.LogWarning(
                "Service payment {PaymentId} was paid but its request {RequestId} is {Status}: the request is left as it is",
                payment.Id, request.Id, request.Status);
        }
    }

    private static bool IsOlderThanLastChange(ObservedIntent observed, ServiceRequestPayment payment) =>
        observed.OccurredAt is { } at && at < payment.UpdatedAt;

    private void LogStale(ServiceRequestPayment payment, ObservedIntent observed) =>
        logger.LogInformation(
            "Stripe event {EventId} ({State}) for payment {PaymentId} is older than its last change and was ignored",
            observed.Source, observed.State, payment.Id);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    public async Task<ServicePaymentEventResult> ApplyDisputeCreatedAsync(
        ServiceDisputeEvent dispute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispute);
        if (string.IsNullOrWhiteSpace(dispute.PaymentIntentId))
            return ServicePaymentEventResult.NotOurs;

        var found = await FindPaymentRefAsync(dispute.PaymentIntentId, null, cancellationToken);
        if (found is null)
            return ServicePaymentEventResult.NotOurs;

        logger.LogError(
            "Dispute {DisputeId} opened on service payment {PaymentId} (PaymentIntent {PaymentIntentId}, account {AccountId}, event {EventId}): {Amount} {Currency}, reason {Reason}, status {Status}. Answer it on the Stripe Dashboard before the deadline",
            dispute.DisputeId, found.Id, dispute.PaymentIntentId, dispute.AccountId ?? "none", dispute.EventId,
            dispute.AmountCents, dispute.Currency ?? "none", dispute.Reason ?? "none", dispute.Status ?? "none");

        await UnderPaymentLockAsync(
            found.ServiceRequestId,
            async () =>
            {
                var payment = await LoadPaymentAsync(found.Id, cancellationToken);
                if (payment is not null)
                {
                    var now = Now();
                    payment.CardDisputeId = Truncate(dispute.DisputeId, 255);
                    payment.CardDisputeStatus = Truncate(dispute.Status, 50);
                    payment.CardDisputeAt = now;
                    payment.UpdatedAt = now;
                }

                return true;
            },
            cancellationToken);

        await RecordDisputeAsync(found.ServiceRequestId, dispute.DisputeId, cancellationToken);

        var detail = $"{dispute.DisputeId} · {dispute.Reason ?? "reason n/a"} · {dispute.Status ?? "status n/a"}";
        return new ServicePaymentEventResult(true, [new ServicePaymentNotice(ServicePaymentNoticeKind.Disputed, found.Id, Detail: Truncate(detail, 200))]);
    }

    /// <summary>
    /// Records the dispute on the reliability of the supplier of the request (once for each request: Stripe may send the event again).
    /// A request from a supplier's showcase is not part of the ranking. Never fails the event: the admins are told whatever happens here.
    /// </summary>
    private async Task RecordDisputeAsync(Guid serviceRequestId, string disputeId, CancellationToken cancellationToken)
    {
        if (reliability is null)
            return;

        try
        {
            var owner = await db.ServiceRequests
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == serviceRequestId)
                .Select(r => new { r.SupplierOrgId, r.RentalContext })
                .FirstOrDefaultAsync(cancellationToken);
            if (owner is null || owner.RentalContext == ServiceRequestRentalContext.Showcase)
                return;

            await reliability.RecordAsync(
                owner.SupplierOrgId,
                SupplierReliabilityEventKind.DisputeOpened,
                serviceRequestId,
                Now(),
                asBackup: false,
                detail: Truncate(disputeId, 200),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The dispute {DisputeId} of request {RequestId} could not be recorded on the supplier's reliability", disputeId, serviceRequestId);
        }
    }

    public void ScheduleSendPendingRequests(Guid supplierOrgId) => jobScheduler.SchedulePendingRequests(supplierOrgId);
}
