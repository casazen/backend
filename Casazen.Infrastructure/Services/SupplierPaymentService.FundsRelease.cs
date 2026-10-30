using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The release of the money CasaZen holds to the supplier (SP-15c-a, decision VT-D1): a <c>Transfer</c> with <c>source_transaction</c> (the
/// charge of the payment) and the <c>transfer_group</c>, made when the payer says "all fine" or <c>ReleaseAfterMinutes</c> after the work is
/// done, whichever comes first, unless a dispute or a refund stands in the way.
/// </summary>
/// <remarks>
/// <para><b>Never two transfers for one payment.</b> Four things hold at once. The release runs under the payment lock and decides from the
/// payment as it is <i>after</i> the lock (<see cref="HeldPaymentRules.Release"/>). Before it creates a transfer it looks for one of the
/// payment's <c>transfer_group</c> on Stripe, so a transfer whose answer was lost, or that outlived Stripe's day-long memory of the key, is
/// found and recorded, not repeated. The creation carries an idempotency key made of the payment and the number of refusals so far. And the
/// database refuses a second transfer id (<c>UIX_ServiceRequestPayments_StripeTransferId</c>) and a second release of the same payment (the
/// status). A transfer that was made and could not be recorded (the save failed) is recorded by the next run.</para>
/// <para><b>Stripe does not retry a failed transfer.</b> A refusal (a 4xx) makes the payment <see cref="ServicePaymentStatus.TransferFailed"/>
/// with the Stripe code, raises the number of refusals and delays the next try (<see cref="HeldPaymentRules.TransferBackoff"/>); a supplier
/// whose account cannot receive money yet, or an answer that does not come, only delays it. After
/// <c>TransferAlertAfterAttempts</c> refusals the admins are told, once.</para>
/// <para>The funds are the supplier's when the funds of the charge are available (about six days in test mode), not at the release: this
/// code promises nothing about when the supplier's bank account is credited.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    /// <summary>The <c>TransferFailureCode</c> of a release that waits for the supplier's Stripe account.</summary>
    private const string AccountNotReadyCode = "account_not_ready";

    /// <summary>The <c>TransferFailureCode</c> of a release that waits for an answer of Stripe that did not come.</summary>
    private const string StripeUnavailableCode = "stripe_unavailable";

    /// <summary>What one try of the release did.</summary>
    private enum ReleaseResult
    {
        /// <summary>The supplier was paid (or had been, and it is recorded now).</summary>
        Released,

        /// <summary>The supplier was paid before: nothing to do.</summary>
        AlreadyReleased,

        /// <summary>Not yet: the wait has not passed, a refund is in progress, or the next try is not due.</summary>
        NotYet,

        /// <summary>The payment is not releasable from where it is (not charged, in dispute, closed, or its request was cancelled).</summary>
        NotReleasable,

        /// <summary>Refunds took everything the supplier would have received.</summary>
        NothingToRelease,

        /// <summary>The supplier's account is not ready, or Stripe did not answer: the payment waits and the job tries again.</summary>
        Waiting,

        /// <summary>Stripe refused the transfer: <see cref="ServicePaymentStatus.TransferFailed"/>, tried again later.</summary>
        Refused,

        /// <summary>A transfer that does not match the payment: held for an admin.</summary>
        NeedsReview,
    }

    public async Task<ServiceRequestPayment> ConfirmOkAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var latest = await FindLatestHeldAsync(requestId, cancellationToken);
        if (latest is not { } known || known.Status == ServicePaymentStatus.Canceled)
            throw NotConfirmable();

        var result = await ReleaseAsync(new PaymentRef(known.Id, requestId), confirmedByPayer: true, clearTracker: false, cancellationToken);
        switch (result)
        {
            case ReleaseResult.Released or ReleaseResult.AlreadyReleased or ReleaseResult.Waiting or ReleaseResult.Refused or ReleaseResult.NeedsReview:
                // The payer's word stands: if the transfer could not be made now, the job makes it; asking again is not an error.
                break;
            default:
                throw NotConfirmable();
        }

        return await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Id == known.Id)
            .FirstAsync(cancellationToken);
    }

    /// <summary>
    /// One try of the release of a payment, under its lock. <paramref name="confirmedByPayer"/> releases without waiting for the moment
    /// the money is due; <paramref name="clearTracker"/> is for the job, which owns its context.
    /// </summary>
    private async Task<ReleaseResult> ReleaseAsync(PaymentRef candidate, bool confirmedByPayer, bool clearTracker, CancellationToken cancellationToken)
    {
        if (clearTracker)
            db.ChangeTracker.Clear();

        ReleaseResult result;
        ServicePaymentNotice? notice;
        await using (var transaction = await LockAsync(candidate.ServiceRequestId, cancellationToken))
        {
            var payment = await LoadPaymentAsync(candidate.Id, cancellationToken);
            if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld })
                return ReleaseResult.NotReleasable;

            var requestStatus = await ReadRequestStatusAsync(payment.ServiceRequestId, cancellationToken);
            var refunds = await LoadRefundsAsync(payment.Id, cancellationToken);
            var inProgress = (int)Math.Min(refunds.Where(IsInProgress).Sum(r => (long)r.AmountCents), int.MaxValue);
            var now = Now();

            var verdict = HeldPaymentRules.Release(
                new HeldReleaseFacts(
                    payment.Status,
                    payment.ReleaseAt,
                    payment.TransferNextAttemptAt,
                    payment.StripeTransferId,
                    payment.NetCents,
                    payment.RefundedCents,
                    inProgress,
                    requestStatus ?? ServiceRequestStatus.Annullato,
                    confirmedByPayer),
                now);
            switch (verdict)
            {
                case HeldReleaseVerdict.AlreadyReleased:
                    return ReleaseResult.AlreadyReleased;
                case HeldReleaseVerdict.NotYet:
                    return ReleaseResult.NotYet;
                case HeldReleaseVerdict.NothingToRelease:
                    return ReleaseResult.NothingToRelease;
                case HeldReleaseVerdict.NotReleasable:
                    return ReleaseResult.NotReleasable;
            }

            // The payer's word stands from here on, whatever happens to the transfer: the job makes it if this try cannot.
            if (confirmedByPayer)
            {
                payment.ReleaseConfirmedAt ??= now;
                payment.ReleaseAt = payment.ReleaseAt is { } due && due < now ? due : now;
            }

            var releasable = HeldPaymentRules.ReleasableCents(payment.NetCents, payment.RefundedCents, inProgress);
            (result, notice) = await TransferAsync(payment, releasable, confirmedByPayer, now, cancellationToken);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        if (notice is not null)
            await CompleteAsync([notice], CancellationToken.None);

        return result;
    }

    /// <summary>
    /// Makes the transfer of <paramref name="amountCents"/> for a payment whose release is due (the caller holds the lock and saves): looks for
    /// the payment's own transfer on Stripe first, creates one if there is none, checks what Stripe says and records it. Returns what happened
    /// and the notice to send after the commit, if any.
    /// </summary>
    private async Task<(ReleaseResult Result, ServicePaymentNotice? Notice)> TransferAsync(
        ServiceRequestPayment payment,
        int amountCents,
        bool confirmedByPayer,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var supplier = await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken);
        if (!supplier.CanReceivePayments || string.IsNullOrWhiteSpace(supplier.AccountId))
        {
            // Nothing is sent to Stripe: the supplier is asked to complete its account (once), and the release waits.
            var first = payment.TransferFailureCode != AccountNotReadyCode;
            Postpone(payment, AccountNotReadyCode, now);
            logger.LogInformation("Payment {PaymentId}: the release waits, the supplier's Stripe account cannot receive money yet", payment.Id);
            return (ReleaseResult.Waiting, first ? new ServicePaymentNotice(ServicePaymentNoticeKind.SupplierAccountNotReady, payment.Id) : null);
        }

        var chargeId = payment.StripeChargeId;
        if (string.IsNullOrWhiteSpace(chargeId))
        {
            // The charge is known from the capture; a payment recorded from the webhook without it reads it from the PaymentIntent.
            PlatformCharge? intent;
            try
            {
                intent = await ReadPlatformIntentAsync(payment.StripePaymentIntentId!, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                Postpone(payment, StripeUnavailableCode, now);
                return (ReleaseResult.Waiting, null);
            }

            chargeId = intent?.LatestChargeId;
            if (string.IsNullOrWhiteSpace(chargeId))
            {
                logger.LogError("Payment {PaymentId} is held but its charge is unknown: the transfer cannot be tied to it", payment.Id);
                return RecordRefusal(payment, "charge_unknown", now);
            }

            payment.StripeChargeId = chargeId;
        }

        var group = payment.TransferGroup ?? ServiceCharges.TransferGroup(payment.ServiceRequestId);

        // 1. The payment's own transfer may exist already: the answer was lost, or the key is older than Stripe remembers it.
        ServiceTransfer? transfer;
        try
        {
            transfer = (await Platform.ListTransfersAsync(group, cancellationToken)).FirstOrDefault(t => LinksToPayment(t, payment.Id));
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Not knowing whether it exists, nothing is created: a second transfer is worse than a late one.
            logger.LogWarning(ex, "Payment {PaymentId}: the transfers of its group could not be read, the release waits", payment.Id);
            Postpone(payment, StripeUnavailableCode, now);
            return (ReleaseResult.Waiting, null);
        }

        // 2. Otherwise this is the first time (or the first since a refusal): create it.
        if (transfer is null)
        {
            try
            {
                transfer = await Platform.CreateTransferAsync(
                    new ServiceTransferRequest(
                        amountCents,
                        payment.Currency,
                        supplier.AccountId,
                        chargeId,
                        group,
                        new Dictionary<string, string>
                        {
                            [ServiceCharges.PaymentMetadataKey] = payment.Id.ToString(),
                            [ServiceCharges.RequestMetadataKey] = payment.ServiceRequestId.ToString(),
                            [ServiceCharges.SupplierMetadataKey] = payment.SupplierOrgId.ToString(),
                        },
                        ServiceCharges.TransferIdempotencyKey(payment.Id, payment.TransferAttempts)),
                    cancellationToken);
            }
            catch (Stripe.StripeException ex) when (IsRejected(ex))
            {
                var code = Truncate(ex.StripeError?.Code ?? ex.StripeError?.Type ?? $"http_{(int)ex.HttpStatusCode}", 100)!;
                logger.LogWarning(
                    "Payment {PaymentId}: Stripe refused the transfer to the supplier ({StripeErrorCode}, HTTP {StatusCode}); it is tried again later",
                    payment.Id, code, (int)ex.HttpStatusCode);
                return RecordRefusal(payment, code, now);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // No answer, or an answer that does not say: the same key will tell on the next try.
                logger.LogWarning(ex, "Payment {PaymentId}: the transfer to the supplier is not confirmed yet, it is tried again", payment.Id);
                Postpone(payment, StripeUnavailableCode, now);
                return (ReleaseResult.Waiting, null);
            }
        }

        // 3. Whoever created it, it must be what CasaZen meant: the amount, the supplier's account, not reversed.
        var mismatches = HeldChargeVerification.TransferMismatches(
            amountCents, supplier.AccountId, transfer.AmountCents, transfer.DestinationAccountId, transfer.Reversed);
        if (mismatches.Count > 0)
        {
            var code = ServiceChargeVerification.ReviewCode(mismatches);
            payment.Status = ServicePaymentStatus.NeedsReview;
            payment.FailureCode = Truncate(code, 100);
            payment.UpdatedAt = now;
            logger.LogError(
                "Payment {PaymentId}: transfer {TransferId} does not match what was meant to be released ({Mismatches}). It is held for a review",
                payment.Id, transfer.Id, code);
            return (ReleaseResult.NeedsReview, new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "transfer:" + code));
        }

        RecordReleased(payment, transfer, now);
        await MarkRequestPaidAsync(
            payment, now, now, cancellationToken, confirmedByPayer ? ServiceRequestActorParty.Customer : ServiceRequestActorParty.System);

        logger.LogInformation(
            "Payment {PaymentId} of request {RequestId} released to the supplier with transfer {TransferId}",
            payment.Id, payment.ServiceRequestId, transfer.Id);
        return (ReleaseResult.Released, new ServicePaymentNotice(ServicePaymentNoticeKind.Released, payment.Id));
    }

    /// <summary>
    /// The transfer carries this payment's id in its metadata: it is the payment's own, not another transfer of the group. A transfer that
    /// was reversed is still the payment's: it is found so that the payment goes to review (<see cref="HeldChargeVerification.ReversedMismatch"/>)
    /// instead of the supplier being paid a second time.
    /// </summary>
    private static bool LinksToPayment(ServiceTransfer transfer, Guid paymentId) =>
        transfer.Metadata.TryGetValue(ServiceCharges.PaymentMetadataKey, out var raw)
        && Guid.TryParse(raw, out var id)
        && id == paymentId;

    /// <summary>The supplier was paid: the payment is released, with the transfer that paid it. <c>PaidAt</c> moves to the day of the release.</summary>
    private static void RecordReleased(ServiceRequestPayment payment, ServiceTransfer transfer, DateTime now)
    {
        payment.Status = ServicePaymentStatus.Released;
        payment.ReleasedAt = now;
        payment.StripeTransferId = transfer.Id;
        payment.PaidAt = now;
        payment.PaidVia = ServicePaymentChannel.Stripe;
        payment.TransferFailureCode = null;
        payment.TransferNextAttemptAt = null;
        payment.FailureCode = null;
        payment.UpdatedAt = now;
    }

    /// <summary>The release cannot go on now and is not Stripe's refusal: it waits the base delay, the payment keeps its status and its key.</summary>
    private void Postpone(ServiceRequestPayment payment, string code, DateTime now)
    {
        payment.TransferFailureCode = code;
        payment.TransferNextAttemptAt = now + HeldPaymentRules.TransferBackoff(1, options.Value.TransferRetryBaseMinutes, options.Value.TransferRetryMaxMinutes);
        payment.UpdatedAt = now;
    }

    /// <summary>
    /// Stripe refused the transfer: the payment is <see cref="ServicePaymentStatus.TransferFailed"/>, the count of refusals (part of the next
    /// idempotency key) grows and the next try waits longer. After <c>TransferAlertAfterAttempts</c> refusals the admins are told, once.
    /// </summary>
    private (ReleaseResult Result, ServicePaymentNotice? Notice) RecordRefusal(ServiceRequestPayment payment, string code, DateTime now)
    {
        payment.TransferAttempts++;
        payment.Status = ServicePaymentStatus.TransferFailed;
        payment.TransferFailureCode = Truncate(code, 100);
        payment.TransferNextAttemptAt = now + HeldPaymentRules.TransferBackoff(
            payment.TransferAttempts, options.Value.TransferRetryBaseMinutes, options.Value.TransferRetryMaxMinutes);
        payment.UpdatedAt = now;

        if (payment.TransferAttempts < options.Value.TransferAlertAfterAttempts || payment.TransferAlertedAt is not null)
            return (ReleaseResult.Refused, null);

        payment.TransferAlertedAt = now;
        logger.LogError(
            "Payment {PaymentId}: the transfer to the supplier failed {Attempts} times (last: {Code}). An admin has to look at it",
            payment.Id, payment.TransferAttempts, code);
        return (ReleaseResult.Refused, new ServicePaymentNotice(ServicePaymentNoticeKind.FundsAlert, payment.Id, Detail: "transfer_failed:" + code));
    }

    private static DomainRuleException NotConfirmable() =>
        new(ServicePaymentErrors.NotConfirmable, ServicePaymentErrors.NotConfirmableMessageKey);
}
