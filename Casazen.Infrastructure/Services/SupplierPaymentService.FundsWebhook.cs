using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the Stripe webhooks tell about the money CasaZen holds (SP-15c-a): the block of the card, the charge, the transfer to the supplier and
/// the card disputes. The events of the platform endpoint have no connected account; each one is applied under the payment lock, exactly once and
/// in any order, and none of them is behind the feature flag (money in flight).
/// </summary>
/// <remarks>
/// <para><b>The state is what Stripe says, checked.</b> A block or a charge is recorded only if it is the payment's own PaymentIntent, on the
/// platform, for the amount and the currency of the payment and with no application fee (<see cref="HeldChargeVerification"/>); otherwise the
/// payment goes to review and the admins are told. A transfer is recorded only if it is the amount and the account that were meant. An event that
/// arrives late (a second <c>succeeded</c>, a <c>canceled</c> after the charge) changes nothing.</para>
/// <para>A card dispute is a fact about the charge, not a decision of CasaZen: it is recorded on the payment for the admin console and the admins
/// are told when money is withdrawn or the dispute is lost. After the release it is CasaZen's loss (VT-D22); nothing is recovered from the
/// supplier automatically.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    // ─── payment_intent.* of a held payment ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a <c>payment_intent.*</c> event to a payment held by CasaZen (the payment is locked and read again by the caller): the block
    /// <c>amount_capturable_updated</c>, the charge <c>succeeded</c> and the block that ended <c>canceled</c>. The other events do not concern
    /// a held payment (a card confirmed by the payer is a block before it is a payment). Returns the notice to send after the commit, if any.
    /// </summary>
    private ServicePaymentNotice? ApplyHeldIntentEvent(ServiceRequestPayment payment, ServicePaymentIntentEvent paymentEvent)
    {
        var now = Now();
        if (!string.Equals(payment.StripePaymentIntentId, paymentEvent.PaymentIntentId, StringComparison.Ordinal))
        {
            // Another PaymentIntent that names this payment: if it was charged, the payer paid twice.
            if (paymentEvent.EventType == "payment_intent.succeeded")
            {
                logger.LogError(
                    "Held payment {PaymentId} has PaymentIntent {Recorded} but {Other} succeeded too (event {EventId}): refund one of the two charges",
                    payment.Id, payment.StripePaymentIntentId ?? "none", paymentEvent.PaymentIntentId, paymentEvent.EventId);
                return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "second_charge");
            }

            return null;
        }

        var observation = new HeldChargeObservation(
            paymentEvent.PaymentIntentId,
            paymentEvent.AccountId,
            paymentEvent.AmountCents,
            paymentEvent.AmountCapturableCents,
            paymentEvent.AmountReceivedCents,
            paymentEvent.Currency,
            paymentEvent.ApplicationFeeCents);

        return paymentEvent.EventType switch
        {
            "payment_intent.amount_capturable_updated" => ApplyBlockEvent(payment, observation, paymentEvent, now),
            "payment_intent.succeeded" => ApplyChargeEvent(payment, observation, paymentEvent, now),
            "payment_intent.canceled" => ApplyBlockEndedEvent(payment, paymentEvent, now),
            "payment_intent.payment_failed" => ApplyHeldPaymentFailed(payment, paymentEvent, now),
            _ => null,
        };
    }

    /// <summary>
    /// The card of a host that is paying a held payment was refused (SP-15c-b): the payment is plainly <c>Failed</c> with the code, and payable
    /// again with the same PaymentIntent. Nothing else: the payer saw the refusal in the Payment Element. A payment that is already charged, or
    /// not waiting for the payer, is left alone (a late event).
    /// </summary>
    private ServicePaymentNotice? ApplyHeldPaymentFailed(ServiceRequestPayment payment, ServicePaymentIntentEvent paymentEvent, DateTime now)
    {
        if (payment.Status is not (ServicePaymentStatus.Requested or ServicePaymentStatus.Processing or ServicePaymentStatus.Failed))
            return null;

        payment.Status = ServicePaymentStatus.Failed;
        payment.FailureCode = Truncate(paymentEvent.FailureCode ?? "payment_failed", 100);
        payment.UpdatedAt = now;
        logger.LogInformation(
            "Held payment {PaymentId} of request {RequestId}: the card was refused (event {EventId})", payment.Id, payment.ServiceRequestId, paymentEvent.EventId);
        return null;
    }

    /// <summary>
    /// The card is blocked. For a payment that exists this is what it already is (it is created <c>Authorized</c> when the e-mail and the
    /// block are both true), so the event only checks the block against the payment and changes nothing when it matches.
    /// </summary>
    private ServicePaymentNotice? ApplyBlockEvent(
        ServiceRequestPayment payment,
        HeldChargeObservation observation,
        ServicePaymentIntentEvent paymentEvent,
        DateTime now)
    {
        if (payment.Status != ServicePaymentStatus.Authorized)
            return null;

        var mismatches = HeldChargeVerification.AuthorizationMismatches(ExpectationOf(payment), observation);
        if (mismatches.Count == 0)
            return null;

        var code = ServiceChargeVerification.ReviewCode(mismatches);
        payment.Status = ServicePaymentStatus.NeedsReview;
        payment.FailureCode = Truncate(code, 100);
        payment.UpdatedAt = now;
        logger.LogError(
            "Held payment {PaymentId}: the block {PaymentIntentId} (event {EventId}) differs from what was asked for ({Mismatches}). It is held for a review",
            payment.Id, paymentEvent.PaymentIntentId, paymentEvent.EventId, code);
        return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: code);
    }

    /// <summary>
    /// The card was charged (by CasaZen's capture, whose own database change may have been rolled back with a take that lost a race, or by
    /// the payer of a payment that is charged at once): the money is held, if it is what was asked for.
    /// </summary>
    private ServicePaymentNotice? ApplyChargeEvent(
        ServiceRequestPayment payment,
        HeldChargeObservation observation,
        ServicePaymentIntentEvent paymentEvent,
        DateTime now)
    {
        switch (payment.Status)
        {
            case ServicePaymentStatus.Held
                or ServicePaymentStatus.TransferFailed
                or ServicePaymentStatus.Disputed
                or ServicePaymentStatus.Released
                or ServicePaymentStatus.PartiallyRefunded
                or ServicePaymentStatus.Refunded
                or ServicePaymentStatus.NeedsReview:
                // The same success again: nothing to do (a payment that needs a review is settled by an admin).
                return null;

            case ServicePaymentStatus.Canceled:
                // The block was dropped and yet the card was charged: money CasaZen holds for nothing. The admins decide.
                logger.LogError(
                    "Held payment {PaymentId} was canceled but PaymentIntent {PaymentIntentId} succeeded (event {EventId}): refund it",
                    payment.Id, paymentEvent.PaymentIntentId, paymentEvent.EventId);
                return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "charged_after_cancel");
        }

        var mismatches = HeldChargeVerification.CaptureMismatches(ExpectationOf(payment), observation);
        if (mismatches.Count > 0)
        {
            var code = ServiceChargeVerification.ReviewCode(mismatches);
            payment.Status = ServicePaymentStatus.NeedsReview;
            payment.FailureCode = Truncate(code, 100);
            payment.UpdatedAt = now;
            logger.LogError(
                "Held payment {PaymentId} NOT recorded as held: PaymentIntent {PaymentIntentId} (event {EventId}) differs from what was asked for ({Mismatches})",
                payment.Id, paymentEvent.PaymentIntentId, paymentEvent.EventId, code);
            return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: code);
        }

        // The money is not in the future: a clock that runs behind Stripe's does not date a payment after now.
        var chargedAt = paymentEvent.OccurredAt is { } at && at < now ? at : now;
        RecordCaptured(payment, paymentEvent.LatestChargeId, chargedAt);
        payment.UpdatedAt = now;
        logger.LogInformation(
            "Held payment {PaymentId} of request {RequestId}: the card was charged (event {EventId})",
            payment.Id, payment.ServiceRequestId, paymentEvent.EventId);
        return null;
    }

    /// <summary>The block ended without a charge (Stripe let it expire, or somebody canceled it outside CasaZen): the payment is dropped.</summary>
    private ServicePaymentNotice? ApplyBlockEndedEvent(ServiceRequestPayment payment, ServicePaymentIntentEvent paymentEvent, DateTime now)
    {
        if (payment.Status != ServicePaymentStatus.Authorized)
        {
            // A charged PaymentIntent cannot be canceled: this is a late event about a block CasaZen already settled.
            return null;
        }

        payment.Status = ServicePaymentStatus.Canceled;
        payment.CanceledAt = now;
        payment.UpdatedAt = now;
        logger.LogInformation(
            "Held payment {PaymentId} of request {RequestId}: the block ended without a charge (event {EventId})",
            payment.Id, payment.ServiceRequestId, paymentEvent.EventId);
        return null;
    }

    /// <summary>
    /// A refund event for a payment held by CasaZen can be recorded once the money was charged. One that reaches CasaZen before the charge it
    /// follows reads the PaymentIntent from the platform and records the charge first, with the checks of the webhook.
    /// </summary>
    private async Task<bool> CanRecordHeldRefundsAsync(
        ServiceRequestPayment payment,
        List<ServicePaymentNotice> notices,
        CancellationToken cancellationToken)
    {
        if (payment.Status is ServicePaymentStatus.Authorized or ServicePaymentStatus.Requested or ServicePaymentStatus.Processing)
        {
            if (string.IsNullOrWhiteSpace(payment.StripePaymentIntentId))
                return false;

            var charge = await ReadPlatformIntentAsync(payment.StripePaymentIntentId, cancellationToken);
            if (charge is not { Status: "succeeded" })
            {
                logger.LogWarning(
                    "Refund event for held payment {PaymentId} that is {Status} and whose PaymentIntent is not succeeded on Stripe: not recorded",
                    payment.Id, payment.Status);
                return false;
            }

            var notice = ApplyChargeEvent(
                payment,
                ObservationOf(charge),
                new ServicePaymentIntentEvent(
                    "refund",
                    "payment_intent.succeeded",
                    charge.Id,
                    AccountId: null,
                    PaymentId: payment.Id,
                    charge.AmountCents,
                    charge.AmountReceivedCents,
                    charge.Currency,
                    charge.ApplicationFeeCents,
                    FailureCode: null,
                    OccurredAt: null,
                    charge.AmountCapturableCents,
                    charge.LatestChargeId),
                Now());
            if (notice is not null)
                notices.Add(notice);
        }

        var recordable = payment.Status is ServicePaymentStatus.Held
            or ServicePaymentStatus.TransferFailed
            or ServicePaymentStatus.Disputed
            or ServicePaymentStatus.Released
            or ServicePaymentStatus.PartiallyRefunded
            or ServicePaymentStatus.Refunded;
        if (!recordable)
        {
            logger.LogWarning(
                "Refund event for held payment {PaymentId} that is {Status}: not recorded (a payment that needs a review is settled by an admin)",
                payment.Id, payment.Status);
        }

        return recordable;
    }

    // ─── transfer.* ─────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServicePaymentEventResult> ApplyTransferEventAsync(
        ServiceTransferEvent transfer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);

        var found = await FindPaymentRefByTransferAsync(transfer, cancellationToken);
        if (found is null)
            return ServicePaymentEventResult.NotOurs;

        var notices = await UnderPaymentLockAsync(
            found.ServiceRequestId,
            async () =>
            {
                var list = new List<ServicePaymentNotice>();
                var payment = await LoadPaymentAsync(found.Id, cancellationToken);
                if (payment is { FundsModel: ServiceFundsModel.PlatformHeld })
                {
                    var notice = transfer.EventType == "transfer.reversed"
                        ? ApplyTransferReversed(payment, transfer)
                        : await ApplyTransferCreatedAsync(payment, transfer, cancellationToken);
                    if (notice is not null)
                        list.Add(notice);
                }

                return list;
            },
            cancellationToken);

        return new ServicePaymentEventResult(true, notices);
    }

    /// <summary>The payment a transfer belongs to: by the payment id CasaZen wrote in its metadata, else by the transfer id already recorded.</summary>
    private async Task<PaymentRef?> FindPaymentRefByTransferAsync(ServiceTransferEvent transfer, CancellationToken cancellationToken)
    {
        if (transfer.PaymentId is { } paymentId)
        {
            var byId = await db.ServiceRequestPayments
                .AsNoTracking()
                .Where(p => p.Id == paymentId)
                .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
                .FirstOrDefaultAsync(cancellationToken);
            if (byId is not null)
                return byId;
        }

        return await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.StripeTransferId == transfer.TransferId)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// <c>transfer.created</c>: the supplier was paid. The release records it when Stripe answers; this records the one whose answer never came
    /// back. A transfer other than the one recorded is a second payment of the same money: the admins are told, nothing else changes.
    /// </summary>
    private async Task<ServicePaymentNotice?> ApplyTransferCreatedAsync(
        ServiceRequestPayment payment,
        ServiceTransferEvent transfer,
        CancellationToken cancellationToken)
    {
        if (payment.StripeTransferId is not null)
        {
            if (string.Equals(payment.StripeTransferId, transfer.TransferId, StringComparison.Ordinal))
                return null;

            logger.LogError(
                "Held payment {PaymentId} was released with transfer {Recorded} but transfer {Other} exists too (event {EventId}): the supplier may have been paid twice",
                payment.Id, payment.StripeTransferId, transfer.TransferId, transfer.EventId);
            return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "second_transfer");
        }

        if (payment.Status is not (ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed))
        {
            logger.LogError(
                "Transfer {TransferId} (event {EventId}) concerns held payment {PaymentId}, which is {Status}: an admin has to look at it",
                transfer.TransferId, transfer.EventId, payment.Id, payment.Status);
            return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "transfer_unexpected");
        }

        var supplier = await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken);
        var refunds = await LoadRefundsAsync(payment.Id, cancellationToken);
        var inProgress = (int)Math.Min(refunds.Where(IsInProgress).Sum(r => (long)r.AmountCents), int.MaxValue);
        var expected = HeldPaymentRules.ReleasableCents(payment.NetCents, payment.RefundedCents, inProgress);

        var mismatches = HeldChargeVerification.TransferMismatches(
            expected, supplier.AccountId, transfer.AmountCents, transfer.DestinationAccountId, transfer.Reversed);
        var now = Now();
        if (mismatches.Count > 0)
        {
            var code = ServiceChargeVerification.ReviewCode(mismatches);
            payment.Status = ServicePaymentStatus.NeedsReview;
            payment.FailureCode = Truncate(code, 100);
            payment.UpdatedAt = now;
            logger.LogError(
                "Transfer {TransferId} (event {EventId}) of held payment {PaymentId} does not match what was meant to be released ({Mismatches})",
                transfer.TransferId, transfer.EventId, payment.Id, code);
            return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "transfer:" + code);
        }

        RecordReleased(payment, new ServiceTransfer(
            transfer.TransferId,
            transfer.AmountCents,
            transfer.Currency ?? payment.Currency,
            transfer.DestinationAccountId,
            transfer.SourceChargeId,
            transfer.TransferGroup,
            transfer.Reversed,
            AmountReversedCents: 0,
            new Dictionary<string, string>()), now);
        await MarkRequestPaidAsync(payment, now, now, cancellationToken, ServiceRequestActorParty.System);
        logger.LogInformation(
            "Held payment {PaymentId} released: transfer {TransferId} recorded from the event {EventId}", payment.Id, transfer.TransferId, transfer.EventId);
        return new ServicePaymentNotice(ServicePaymentNoticeKind.Released, payment.Id);
    }

    /// <summary>
    /// <c>transfer.reversed</c>: somebody took the money back from the supplier (the Stripe Dashboard, or an automatic recovery). CasaZen does
    /// not pay the supplier again nor refund anyone on its own: it logs an error and tells the admins.
    /// </summary>
    private ServicePaymentNotice? ApplyTransferReversed(ServiceRequestPayment payment, ServiceTransferEvent transfer)
    {
        if (!string.Equals(payment.StripeTransferId, transfer.TransferId, StringComparison.Ordinal))
            return null;

        logger.LogError(
            "Transfer {TransferId} of held payment {PaymentId} was reversed (event {EventId}): an admin has to decide what happens to the money",
            transfer.TransferId, payment.Id, transfer.EventId);
        return new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "transfer_reversed");
    }

    // ─── charge.dispute.* ───────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServicePaymentEventResult> ApplyDisputeChangedAsync(
        ServiceDisputeEvent dispute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispute);
        if (string.IsNullOrWhiteSpace(dispute.PaymentIntentId))
            return ServicePaymentEventResult.NotOurs;

        var found = await FindPaymentRefAsync(dispute.PaymentIntentId, null, cancellationToken);
        if (found is null)
            return ServicePaymentEventResult.NotOurs;

        var notices = await UnderPaymentLockAsync(
            found.ServiceRequestId,
            async () =>
            {
                var list = new List<ServicePaymentNotice>();
                var payment = await LoadPaymentAsync(found.Id, cancellationToken);
                if (payment is not null)
                    RecordCardDispute(payment, dispute, list);

                return list;
            },
            cancellationToken);

        return new ServicePaymentEventResult(true, notices);
    }

    /// <summary>
    /// Records the card dispute on the payment (its id, Stripe's status and when), for the admin console. The admins are told when money is
    /// withdrawn or the dispute is lost; after the release that loss is CasaZen's (VT-D22) and nothing is recovered automatically.
    /// </summary>
    private void RecordCardDispute(ServiceRequestPayment payment, ServiceDisputeEvent dispute, List<ServicePaymentNotice> notices)
    {
        var now = Now();
        var changed = payment.CardDisputeId != dispute.DisputeId || payment.CardDisputeStatus != dispute.Status;
        payment.CardDisputeId = Truncate(dispute.DisputeId, 255);
        payment.CardDisputeStatus = Truncate(dispute.Status, 50);
        payment.CardDisputeAt = now;
        payment.UpdatedAt = now;

        var lost = dispute.EventType == "charge.dispute.funds_withdrawn"
                   || (dispute.EventType == "charge.dispute.closed" && dispute.Status == "lost");
        if (changed && lost)
        {
            var afterRelease = payment.Status == ServicePaymentStatus.Released;
            logger.LogError(
                "Card dispute {DisputeId} on held payment {PaymentId} ({Status}, event {EventId}): money was withdrawn from CasaZen{AfterRelease}",
                dispute.DisputeId, payment.Id, dispute.Status ?? "none", dispute.EventId, afterRelease ? " after it was released to the supplier" : string.Empty);
            notices.Add(new ServicePaymentNotice(
                ServicePaymentNoticeKind.FundsAlert,
                payment.Id,
                Detail: Truncate($"card_dispute_lost:{dispute.DisputeId}:{(afterRelease ? "after_release" : "before_release")}", 200)));
        }
    }
}
