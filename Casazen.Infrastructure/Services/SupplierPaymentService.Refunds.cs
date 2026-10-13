using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Refunds of the service payments (SP-15b): the admin's refund (<see cref="ISupplierPaymentRefundService"/>) and the
/// <c>charge.refunded</c> / <c>refund.*</c> events that tell CasaZen what Stripe did, from CasaZen or from outside.
/// </summary>
/// <remarks>
/// <para>A refund is a row (<see cref="ServiceRequestPaymentRefund"/>); the payment's <c>RefundedCents</c> and its
/// <c>PartiallyRefunded</c> / <c>Refunded</c> status are always recomputed from the succeeded rows, never added to, so a refund
/// event that arrives twice, late or out of order cannot count the same euros twice or lose them. Applying one is under the
/// payment lock of the request. The request stays <c>Pagato</c>.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    // ─── The admin refunds a payment ────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceRequestPaymentRefund> RefundAsync(
        Guid paymentId,
        int? amountCents,
        string? reason,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var note = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note?.Length ?? 0, ServicePaymentLimits.OfflineNoteMaxLength, nameof(reason));

        var requestId = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Id == paymentId)
            .Select(p => (Guid?)p.ServiceRequestId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PaymentNotFound();

        // 1. Reserve: under the lock, the refundable amount is checked and a pending row written, so two requests never refund the
        //    same euros and the row's number is the idempotency key of the Stripe request.
        db.ChangeTracker.Clear();
        ServiceRequestPaymentRefund refund;
        await using (var transaction = await LockAsync(requestId, cancellationToken))
        {
            var payment = await LoadPaymentAsync(paymentId, cancellationToken) ?? throw PaymentNotFound();
            var refunds = await LoadRefundsAsync(paymentId, cancellationToken);

            refund = Reserve(payment, refunds, amountCents, note, actorUserId, Now());
            db.ServiceRequestPaymentRefunds.Add(refund);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation(
            "Refund {RefundId} (#{Sequence}) of {AmountCents} cents reserved on service payment {PaymentId} by admin {ActorUserId}",
            refund.Id, refund.Sequence, refund.AmountCents, paymentId, actorUserId);

        // 2. Ask Stripe, outside the lock: a slow answer holds nothing.
        return await SubmitRefundAsync(refund.Id, resubmission: false, throwOnTransient: false, cancellationToken);
    }

    /// <summary>
    /// Checks the refund against the payment and builds the pending row (the caller holds the payment lock and saves). The amount
    /// still refundable is what was paid minus what was refunded and what is being refunded.
    /// </summary>
    private ServiceRequestPaymentRefund Reserve(
        ServiceRequestPayment payment,
        IReadOnlyList<ServiceRequestPaymentRefund> refunds,
        int? amountCents,
        string? note,
        string actorUserId,
        DateTime now)
    {
        // Paid outside CasaZen: no money went through CasaZen, nothing to give back from here.
        if (payment.PaidVia == ServicePaymentChannel.Offline
            && payment.Status is ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded)
            throw new DomainRuleException(ServicePaymentErrors.RefundOffline, ServicePaymentErrors.RefundOfflineMessageKey);

        if (payment.Status == ServicePaymentStatus.Refunded)
            throw new DomainRuleException(ServicePaymentErrors.RefundNothing, ServicePaymentErrors.RefundNothingMessageKey);

        if (payment.Status is not (ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded)
            || payment.PaidVia != ServicePaymentChannel.Stripe
            || string.IsNullOrWhiteSpace(payment.StripePaymentIntentId)
            || string.IsNullOrWhiteSpace(payment.ConnectedAccountId))
            throw new DomainRuleException(ServicePaymentErrors.RefundNotRefundable, ServicePaymentErrors.RefundNotRefundableMessageKey);

        var refunded = refunds.Where(r => r.Status == ServicePaymentRefundStatus.Succeeded).Sum(r => (long)r.AmountCents);
        var inProgress = refunds.Where(IsInProgress).Sum(r => (long)r.AmountCents);
        var refundable = payment.AmountCents - refunded - inProgress;
        if (refundable <= 0)
            throw new DomainRuleException(ServicePaymentErrors.RefundNothing, ServicePaymentErrors.RefundNothingMessageKey);

        var amount = amountCents ?? (int)refundable;
        if (amount <= 0)
            throw new DomainRuleException(ServicePaymentErrors.RefundAmountInvalid, ServicePaymentErrors.RefundAmountInvalidMessageKey);

        if (amount > refundable)
        {
            throw new DomainRuleException(
                ServicePaymentErrors.RefundAmountExceeds,
                ServicePaymentErrors.RefundAmountExceedsMessageKey,
                refundable / 100m);
        }

        var sequence = NextSequence(refunds);
        return new ServiceRequestPaymentRefund
        {
            ServiceRequestPaymentId = payment.Id,
            Sequence = sequence,
            AmountCents = amount,
            Status = ServicePaymentRefundStatus.Pending,
            Origin = ServicePaymentRefundOrigin.Admin,
            IdempotencyKey = ServiceCharges.RefundIdempotencyKey(payment.Id, sequence),
            Reason = note,
            RequestedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Sends a pending refund to Stripe with its idempotency key and records the answer. A 4xx (Stripe created nothing) fails the
    /// refund; a timeout or a 5xx keeps it pending, to be resent with the same key by the sync job, or is rethrown when
    /// <paramref name="throwOnTransient"/> (the sync job itself). A <paramref name="resubmission"/> first looks for the refund on
    /// Stripe by the id in its metadata: Stripe keeps an idempotency key for about a day only, and a refund whose answer was lost
    /// may exist.
    /// </summary>
    private async Task<ServiceRequestPaymentRefund> SubmitRefundAsync(
        Guid refundId,
        bool resubmission,
        bool throwOnTransient,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var row = await db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.Id == refundId).FirstAsync(cancellationToken);
        if (row.Status != ServicePaymentRefundStatus.Pending || row.StripeRefundId is not null)
            return row;

        var payment = await db.ServiceRequestPayments.AsNoTracking().Where(p => p.Id == row.ServiceRequestPaymentId).FirstAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payment.StripePaymentIntentId) || string.IsNullOrWhiteSpace(payment.ConnectedAccountId)
            || string.IsNullOrWhiteSpace(row.IdempotencyKey))
        {
            // Not reachable through the API (the reservation checks them); a row like this is closed rather than retried forever.
            await FailRefundAsync(row.Id, payment.ServiceRequestId, "not_sendable", cancellationToken);
            return await ReadRefundAsync(refundId, cancellationToken);
        }

        ServiceChargeRefund? answer = null;
        try
        {
            if (resubmission)
            {
                var existing = await gateway.ListRefundsAsync(payment.StripePaymentIntentId, payment.ConnectedAccountId, cancellationToken);
                answer = existing.FirstOrDefault(r => LinksTo(r, row.Id));
            }

            answer ??= await gateway.CreateRefundAsync(
                new ServiceChargeRefundRequest(
                    payment.StripePaymentIntentId,
                    payment.ConnectedAccountId,
                    row.AmountCents,
                    RefundApplicationFee: payment.ApplicationFeeCents > 0,
                    row.IdempotencyKey,
                    new Dictionary<string, string>
                    {
                        [ServiceCharges.RefundMetadataKey] = row.Id.ToString(),
                        [ServiceCharges.PaymentMetadataKey] = payment.Id.ToString(),
                        [ServiceCharges.RequestMetadataKey] = payment.ServiceRequestId.ToString(),
                        [ServiceCharges.SupplierMetadataKey] = payment.SupplierOrgId.ToString(),
                    }),
                cancellationToken);
        }
        catch (Stripe.StripeException ex) when (IsRejected(ex))
        {
            var code = Truncate(ex.StripeError?.Code ?? ex.StripeError?.Type ?? $"http_{(int)ex.HttpStatusCode}", 100)!;
            await FailRefundAsync(row.Id, payment.ServiceRequestId, code, cancellationToken);
            logger.LogWarning(
                "Stripe rejected refund {RefundId} of service payment {PaymentId}: {StripeErrorCode} (HTTP {StatusCode}); nothing was refunded",
                row.Id, payment.Id, code, (int)ex.HttpStatusCode);
            return await ReadRefundAsync(refundId, cancellationToken);
        }
        catch (Exception ex) when (!throwOnTransient && IsTransient(ex))
        {
            // Stripe may or may not have created it: the same key will tell. The amount stays reserved.
            logger.LogWarning(
                ex, "Refund {RefundId} of service payment {PaymentId} not confirmed by Stripe yet: it stays pending and the sync job resends it",
                row.Id, payment.Id);
            return row;
        }

        // 3. Record the answer, under the lock again, so a webhook that arrives meanwhile and this answer are applied one at a time.
        db.ChangeTracker.Clear();
        var notices = await UnderPaymentLockAsync(
            payment.ServiceRequestId,
            async () =>
            {
                var list = new List<ServicePaymentNotice>();
                var locked = await LoadPaymentAsync(payment.Id, cancellationToken)
                    ?? throw PaymentNotFound();
                await ApplyRefundSnapshotsAsync(locked, [answer], list, cancellationToken);
                return list;
            },
            cancellationToken);

        await CompleteAsync(notices, CancellationToken.None);
        return await ReadRefundAsync(refundId, cancellationToken);
    }

    /// <summary>A pending refund Stripe will never create: closed as failed, its amount free again.</summary>
    private async Task FailRefundAsync(Guid refundId, Guid requestId, string code, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await UnderPaymentLockAsync(
            requestId,
            async () =>
            {
                var row = await db.ServiceRequestPaymentRefunds.Where(r => r.Id == refundId).FirstOrDefaultAsync(cancellationToken);
                if (row is null || row.Status != ServicePaymentRefundStatus.Pending || row.StripeRefundId is not null)
                    return false;

                row.Status = ServicePaymentRefundStatus.Failed;
                row.FailureCode = code;
                row.UpdatedAt = Now();
                return true;
            },
            cancellationToken);
    }

    private async Task<ServiceRequestPaymentRefund> ReadRefundAsync(Guid refundId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.Id == refundId).FirstAsync(cancellationToken);
    }

    // ─── charge.refunded and refund.* ───────────────────────────────────────────────────────────────────────────────

    public async Task<ServicePaymentEventResult> ApplyChargeRefundedAsync(
        string paymentIntentId,
        string? accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        var found = await FindPaymentRefAsync(paymentIntentId, null, cancellationToken);
        if (found is null)
            return ServicePaymentEventResult.NotOurs;

        var notices = await UnderPaymentLockAsync(
            found.ServiceRequestId,
            async () =>
            {
                var list = new List<ServicePaymentNotice>();
                var payment = await LoadPaymentAsync(found.Id, cancellationToken);
                if (payment is null || !await CanRecordRefundsAsync(payment, accountId, list, cancellationToken))
                    return list;

                // Since API version 2022-11-15 a charge does not embed its refunds: they are read from Stripe, on the supplier's
                // account, so what is recorded is what Stripe has whatever the order of the events.
                var snapshots = await gateway.ListRefundsAsync(payment.StripePaymentIntentId!, payment.ConnectedAccountId!, cancellationToken);
                await ApplyRefundSnapshotsAsync(payment, snapshots, list, cancellationToken);
                return list;
            },
            cancellationToken);

        return new ServicePaymentEventResult(true, notices);
    }

    public async Task<ServicePaymentEventResult> ApplyRefundChangedAsync(
        ServiceChargeRefund refund,
        string? accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refund);
        if (string.IsNullOrWhiteSpace(refund.PaymentIntentId))
            return ServicePaymentEventResult.NotOurs;

        var found = await FindPaymentRefAsync(refund.PaymentIntentId, null, cancellationToken);
        if (found is null)
            return ServicePaymentEventResult.NotOurs;

        var notices = await UnderPaymentLockAsync(
            found.ServiceRequestId,
            async () =>
            {
                var list = new List<ServicePaymentNotice>();
                var payment = await LoadPaymentAsync(found.Id, cancellationToken);
                if (payment is not null && await CanRecordRefundsAsync(payment, accountId, list, cancellationToken))
                    await ApplyRefundSnapshotsAsync(payment, [refund], list, cancellationToken);

                return list;
            },
            cancellationToken);

        return new ServicePaymentEventResult(true, notices);
    }

    /// <summary>
    /// A refund is recorded only for a payment that is paid, on an event that comes from the account the payment lives on. A refund
    /// can reach CasaZen before the success it follows (the events of one payment are processed in any order): then the
    /// PaymentIntent is read from Stripe and, if it did succeed, recorded as paid first, with the same checks as the webhook.
    /// </summary>
    private async Task<bool> CanRecordRefundsAsync(
        ServiceRequestPayment payment,
        string? eventAccountId,
        List<ServicePaymentNotice> notices,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(payment.ConnectedAccountId, eventAccountId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Refund event of service payment {PaymentId} came from account {EventAccount}, expected {ExpectedAccount}: ignored",
                payment.Id, eventAccountId ?? "none", payment.ConnectedAccountId ?? "none");
            return false;
        }

        if (payment.Status is ServicePaymentStatus.Requested or ServicePaymentStatus.Processing or ServicePaymentStatus.Failed)
        {
            if (string.IsNullOrWhiteSpace(payment.StripePaymentIntentId))
                return false;

            var intent = await ReadIntentAsync(payment.StripePaymentIntentId, payment.ConnectedAccountId!, cancellationToken);
            if (intent is not { Status: "succeeded" })
            {
                logger.LogWarning(
                    "Refund event for service payment {PaymentId} that is {Status} and whose PaymentIntent is not succeeded on Stripe: not recorded",
                    payment.Id, payment.Status);
                return false;
            }

            var notice = await ApplyObservationAsync(payment, Observe(intent, payment) with { AccountId = eventAccountId, Source = "refund" }, cancellationToken);
            if (notice is not null)
                notices.Add(notice);
        }

        var recordable = payment.Status is ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded;
        if (!recordable)
        {
            logger.LogWarning(
                "Refund event for service payment {PaymentId} that is {Status}: not recorded (a payment that needs a review is settled by an admin)",
                payment.Id, payment.Status);
        }

        return recordable;
    }

    // ─── Recording Stripe's refunds ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the refunds Stripe reports to the rows of the payment (a refund made outside CasaZen gets a row of its own), then
    /// recomputes the payment from the succeeded ones. The payment is locked and read again by the caller. Applying a refund twice
    /// changes nothing.
    /// </summary>
    private async Task ApplyRefundSnapshotsAsync(
        ServiceRequestPayment payment,
        IEnumerable<ServiceChargeRefund> snapshots,
        List<ServicePaymentNotice> notices,
        CancellationToken cancellationToken)
    {
        var now = Now();
        var refunds = await LoadRefundsAsync(payment.Id, cancellationToken);

        foreach (var snapshot in snapshots)
        {
            if (!string.Equals(snapshot.PaymentIntentId, payment.StripePaymentIntentId, StringComparison.Ordinal))
                continue;

            var row = FindRefund(refunds, snapshot);
            if (row is null)
            {
                if (snapshot.AmountCents <= 0)
                    continue;

                // Made outside CasaZen (Stripe Dashboard or API): recorded so that the payment reflects it.
                row = new ServiceRequestPaymentRefund
                {
                    ServiceRequestPaymentId = payment.Id,
                    Sequence = NextSequence(refunds),
                    Origin = ServicePaymentRefundOrigin.Stripe,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.ServiceRequestPaymentRefunds.Add(row);
                refunds.Add(row);
                logger.LogInformation(
                    "Refund {StripeRefundId} of service payment {PaymentId} was made outside CasaZen: recorded", snapshot.Id, payment.Id);
            }

            if (ApplyRefund(row, snapshot, now))
                notices.Add(new ServicePaymentNotice(ServicePaymentNoticeKind.Refunded, payment.Id, RefundId: row.Id));
        }

        RecomputeRefunds(payment, refunds, now);
    }

    /// <summary>
    /// Moves a refund row to what Stripe says; true when it has just become succeeded. A refund that is already final is not taken
    /// back to pending by an older event (events arrive in any order); a failure after a success is taken, since Stripe says so.
    /// </summary>
    private bool ApplyRefund(ServiceRequestPaymentRefund row, ServiceChargeRefund snapshot, DateTime now)
    {
        var wasSucceeded = row.Status == ServicePaymentRefundStatus.Succeeded;
        var incoming = MapRefundStatus(snapshot.Status);

        row.StripeRefundId ??= snapshot.Id;
        if (snapshot.AmountCents > 0)
            row.AmountCents = (int)Math.Min(snapshot.AmountCents, int.MaxValue);

        var alreadyFinal = row.Status is ServicePaymentRefundStatus.Succeeded or ServicePaymentRefundStatus.Failed or ServicePaymentRefundStatus.Canceled;
        if (alreadyFinal && incoming is ServicePaymentRefundStatus.Pending or ServicePaymentRefundStatus.RequiresAction)
        {
            row.UpdatedAt = now;
            return false;
        }

        row.Status = incoming;
        row.FailureCode = incoming is ServicePaymentRefundStatus.Failed or ServicePaymentRefundStatus.Canceled
            ? Truncate(snapshot.FailureReason ?? snapshot.Status ?? "failed", 100)
            : null;
        row.CompletedAt = incoming == ServicePaymentRefundStatus.Succeeded ? row.CompletedAt ?? now : null;
        row.UpdatedAt = now;

        if (!wasSucceeded && incoming == ServicePaymentRefundStatus.Succeeded)
        {
            logger.LogInformation("Refund {RefundId} ({StripeRefundId}) of {AmountCents} cents succeeded on Stripe", row.Id, row.StripeRefundId, row.AmountCents);
            return true;
        }

        if (wasSucceeded && incoming != ServicePaymentRefundStatus.Succeeded)
            logger.LogWarning("Refund {RefundId} ({StripeRefundId}) moved from succeeded to {Status}", row.Id, row.StripeRefundId, incoming);

        return false;
    }

    /// <summary>
    /// <c>RefundedCents</c> and the status of the payment from its succeeded refunds, and the share of the commission each of them
    /// gives back: the commission in proportion to the total refunded so far (all of it for a refund in full), the parts adding up
    /// to that figure whatever the order the refunds succeeded in. A payment that is paid goes back to <c>Paid</c> if every refund
    /// failed.
    /// </summary>
    private void RecomputeRefunds(ServiceRequestPayment payment, IReadOnlyList<ServiceRequestPaymentRefund> refunds, DateTime now)
    {
        var succeeded = refunds
            .Where(r => r.Status == ServicePaymentRefundStatus.Succeeded)
            .OrderBy(r => r.CompletedAt ?? r.CreatedAt)
            .ThenBy(r => r.Sequence)
            .ToList();

        long cumulative = 0;
        var feeSoFar = 0;
        foreach (var refund in succeeded)
        {
            cumulative += refund.AmountCents;
            var feeBack = SupplierCommission.FeeRefundedCents(
                payment.ApplicationFeeCents, payment.AmountCents, (int)Math.Min(cumulative, payment.AmountCents));
            refund.ApplicationFeeRefundedCents = feeBack - feeSoFar;
            feeSoFar = feeBack;
        }

        foreach (var refund in refunds.Where(r => r.Status != ServicePaymentRefundStatus.Succeeded))
            refund.ApplicationFeeRefundedCents = null;

        if (cumulative > payment.AmountCents)
        {
            logger.LogError(
                "Service payment {PaymentId} was refunded {Refunded} cents, more than the {Amount} it was: recorded as refunded in full",
                payment.Id, cumulative, payment.AmountCents);
        }

        var refunded = (int)Math.Min(cumulative, payment.AmountCents);
        var status = payment.Status;
        if (status is ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded)
        {
            status = refunded <= 0
                ? ServicePaymentStatus.Paid
                : refunded >= payment.AmountCents ? ServicePaymentStatus.Refunded : ServicePaymentStatus.PartiallyRefunded;
        }

        // A refund event that says nothing new writes nothing to the payment.
        if (payment.RefundedCents != refunded || payment.Status != status)
        {
            payment.RefundedCents = refunded;
            payment.Status = status;
            payment.UpdatedAt = now;
        }
    }

    /// <summary>The refunds of the payment, including the ones added to this context and not saved yet; the ones already tracked are read again.</summary>
    private async Task<List<ServiceRequestPaymentRefund>> LoadRefundsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        foreach (var tracked in db.ServiceRequestPaymentRefunds.Local
                     .Where(r => r.ServiceRequestPaymentId == paymentId && db.Entry(r).State == EntityState.Unchanged)
                     .ToList())
        {
            await db.Entry(tracked).ReloadAsync(cancellationToken);
        }

        var stored = await db.ServiceRequestPaymentRefunds.Where(r => r.ServiceRequestPaymentId == paymentId).ToListAsync(cancellationToken);
        var added = db.ServiceRequestPaymentRefunds.Local
            .Where(r => r.ServiceRequestPaymentId == paymentId && !stored.Contains(r))
            .ToList();
        return stored.Concat(added).ToList();
    }

    private static ServiceRequestPaymentRefund? FindRefund(IReadOnlyList<ServiceRequestPaymentRefund> refunds, ServiceChargeRefund snapshot)
    {
        var byStripeId = refunds.FirstOrDefault(r => r.StripeRefundId == snapshot.Id);
        if (byStripeId is not null)
            return byStripeId;

        // Created by CasaZen but the answer never came back (a timeout): linked through the id it put in the metadata.
        return TryReadRefundId(snapshot) is { } id
            ? refunds.FirstOrDefault(r => r.Id == id && r.StripeRefundId is null)
            : null;
    }

    private static bool LinksTo(ServiceChargeRefund snapshot, Guid refundId) => TryReadRefundId(snapshot) == refundId;

    private static Guid? TryReadRefundId(ServiceChargeRefund snapshot) =>
        snapshot.Metadata is not null
        && snapshot.Metadata.TryGetValue(ServiceCharges.RefundMetadataKey, out var raw)
        && Guid.TryParse(raw, out var id)
            ? id
            : null;

    private static int NextSequence(IReadOnlyList<ServiceRequestPaymentRefund> refunds) =>
        refunds.Count == 0 ? 1 : refunds.Max(r => r.Sequence) + 1;

    private static bool IsInProgress(ServiceRequestPaymentRefund refund) =>
        refund.Status is ServicePaymentRefundStatus.Pending or ServicePaymentRefundStatus.RequiresAction;

    private static ServicePaymentRefundStatus MapRefundStatus(string? status) => status switch
    {
        "succeeded" => ServicePaymentRefundStatus.Succeeded,
        "failed" => ServicePaymentRefundStatus.Failed,
        "canceled" => ServicePaymentRefundStatus.Canceled,
        "requires_action" => ServicePaymentRefundStatus.RequiresAction,
        _ => ServicePaymentRefundStatus.Pending,
    };

    /// <summary>Stripe answered and created nothing (4xx other than 429): the refund failed.</summary>
    private static bool IsRejected(Stripe.StripeException ex)
    {
        var status = (int)ex.HttpStatusCode;
        return status is >= 400 and < 500 && ex.HttpStatusCode != HttpStatusCode.TooManyRequests;
    }

    /// <summary>No answer, or an answer that does not say whether the refund exists: try again with the same key.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is Stripe.StripeException or HttpRequestException or TimeoutException or OperationCanceledException;
}
