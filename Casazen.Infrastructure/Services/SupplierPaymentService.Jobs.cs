using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The jobs of the service payments (SP-15b): <c>service-payment-sync</c> (every 15 minutes), <c>service-payment-reminders</c>
/// (daily, 07:30 UTC) and <c>SendPendingPaymentRequestsJob(orgId)</c> (queued by <c>account.updated</c>).
/// </summary>
/// <remarks>
/// <para>Every payment is handled on its own: under the payment lock of its request, in its own transaction, read again after the
/// lock, and with the change tracker cleared before it. A run that overlaps another run, a webhook or a request of the supplier
/// finds the payment already changed and skips it, so a link is never sent twice and a payment is never recorded twice. An error on
/// one payment is logged and counted, and the others go on.</para>
/// <para>The feature flag <c>SupplierOnlinePayments</c> stops what creates a payment request (the first requests and the reminders);
/// the sync of the money in flight and the late flag are not behind it.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    /// <summary>Which email with a payment link is being sent.</summary>
    private enum LinkKind
    {
        /// <summary>The first request of a payment that was pending (the supplier was not ready, or the payer had no address).</summary>
        FirstRequest,

        /// <summary>A reminder of a payment still to be made.</summary>
        Reminder,

        /// <summary>A new link after a payment that was in flight failed (a returned SEPA debit).</summary>
        Retry,
    }

    private enum LinkOutcome
    {
        Sent,
        Skipped,
        NotQueued,
    }

    // ─── service-payment-sync ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServicePaymentSyncRun> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        int paymentsRead = 0, paymentsUpdated = 0, refundsRead = 0, refundsUpdated = 0, errors = 0;

        // 1. The payments in flight: Stripe tells what became of them, in case the event was lost.
        var processing = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Status == ServicePaymentStatus.Processing && p.StripePaymentIntentId != null && p.ConnectedAccountId != null)
            .OrderBy(p => p.UpdatedAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .ToListAsync(cancellationToken);

        foreach (var candidate in processing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (notice, changed) = await SynchronizePaymentAsync(candidate, cancellationToken);
                paymentsRead++;
                if (changed)
                    paymentsUpdated++;
                if (notice is not null)
                    await CompleteAsync([notice], CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Service payment {PaymentId} in flight could not be read from Stripe; the next run retries it", candidate.Id);
            }
        }

        // 2. The refunds that never got Stripe's answer: looked for on Stripe, then sent again with the same key.
        var resubmitBefore = Now().AddMinutes(-ServicePaymentLimits.RefundResubmitAfterMinutes);
        var unsent = await db.ServiceRequestPaymentRefunds
            .AsNoTracking()
            .Where(r => r.Status == ServicePaymentRefundStatus.Pending && r.StripeRefundId == null && r.CreatedAt <= resubmitBefore)
            .OrderBy(r => r.CreatedAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        foreach (var refundId in unsent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var after = await SubmitRefundAsync(refundId, resubmission: true, throwOnTransient: true, cancellationToken);
                refundsRead++;
                if (after.Status != ServicePaymentRefundStatus.Pending)
                    refundsUpdated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Refund {RefundId} still waiting for Stripe could not be sent again; the next run retries it", refundId);
            }
        }

        // 3. The refunds Stripe has not completed yet (pending, waiting for the bank): read again.
        var open = await db.ServiceRequestPaymentRefunds
            .AsNoTracking()
            .Where(r => (r.Status == ServicePaymentRefundStatus.Pending || r.Status == ServicePaymentRefundStatus.RequiresAction) && r.StripeRefundId != null)
            .OrderBy(r => r.UpdatedAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(r => r.ServiceRequestPaymentId)
            .ToListAsync(cancellationToken);

        foreach (var paymentId in open.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await RefreshRefundsAsync(paymentId, cancellationToken))
                    refundsUpdated++;
                refundsRead++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Refunds of service payment {PaymentId} could not be read from Stripe; the next run retries them", paymentId);
            }
        }

        return new ServicePaymentSyncRun(paymentsRead, paymentsUpdated, refundsRead, refundsUpdated, errors);
    }

    /// <summary>Reads the PaymentIntent of a payment in flight and applies what it says, exactly as the webhook would.</summary>
    private async Task<(ServicePaymentNotice? Notice, bool Changed)> SynchronizePaymentAsync(PaymentRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await UnderPaymentLockAsync(
            candidate.ServiceRequestId,
            async () =>
            {
                var payment = await LoadPaymentAsync(candidate.Id, cancellationToken);
                // Read again under the lock: the webhook may have settled it while this run was waiting.
                if (payment is not { Status: ServicePaymentStatus.Processing, StripePaymentIntentId: { } intentId, ConnectedAccountId: { } accountId })
                    return ((ServicePaymentNotice?)null, false);

                var intent = await ReadIntentAsync(intentId, accountId, cancellationToken);
                if (intent is null)
                {
                    // The PaymentIntent is gone with its account: money in flight that CasaZen can no longer follow.
                    payment.Status = ServicePaymentStatus.NeedsReview;
                    payment.FailureCode = ServiceChargeVerification.ReviewPrefix + "intent_gone";
                    payment.UpdatedAt = Now();
                    logger.LogError(
                        "Service payment {PaymentId} was in flight but PaymentIntent {PaymentIntentId} no longer exists on Stripe: it needs a review",
                        payment.Id, intentId);
                    return (new ServicePaymentNotice(ServicePaymentNoticeKind.NeedsReview, payment.Id, Detail: "intent_gone"), true);
                }

                var before = payment.Status;
                var notice = await ApplyObservationAsync(payment, Observe(intent, payment), cancellationToken);
                return (notice, payment.Status != before);
            },
            cancellationToken);
    }

    /// <summary>Lists the refunds of a payment on Stripe and applies them (a pending one may have completed). True when anything changed.</summary>
    private async Task<bool> RefreshRefundsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var requestId = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Id == paymentId)
            .Select(p => (Guid?)p.ServiceRequestId)
            .FirstOrDefaultAsync(cancellationToken);
        if (requestId is not { } id)
            return false;

        var notices = await UnderPaymentLockAsync(
            id,
            async () =>
            {
                var list = new List<ServicePaymentNotice>();
                var payment = await LoadPaymentAsync(paymentId, cancellationToken);
                if (payment is not { StripePaymentIntentId: { } intentId, ConnectedAccountId: { } accountId })
                    return list;

                var snapshots = await gateway.ListRefundsAsync(intentId, accountId, cancellationToken);
                await ApplyRefundSnapshotsAsync(payment, snapshots, list, cancellationToken);
                return list;
            },
            cancellationToken);

        await CompleteAsync(notices, CancellationToken.None);
        return notices.Count > 0;
    }

    // ─── service-payment-reminders ──────────────────────────────────────────────────────────────────────────────────

    public async Task<ServicePaymentReminderRun> RunRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = Now();
        var emailsEnabled = features.IsEnabled(FeatureFlags.SupplierOnlinePayments);
        int markedLate = 0, requestsSent = 0, remindersSent = 0, skipped = 0, errors = 0;

        // 1. Late: asked for LateAfterDays ago and still unpaid. Not behind the flag: it is a state of the payment, not a message.
        var lateBefore = now.AddDays(-options.Value.LateAfterDays);
        var late = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.LateAt == null && p.RequestedAt != null && p.RequestedAt <= lateBefore
                        && (p.Status == ServicePaymentStatus.Requested || p.Status == ServicePaymentStatus.Failed))
            .OrderBy(p => p.RequestedAt)
            .Take(ServicePaymentLimits.ReminderBatchSize)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .ToListAsync(cancellationToken);

        foreach (var candidate in late)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await MarkLateAsync(candidate, cancellationToken))
                    markedLate++;
                else
                    skipped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Service payment {PaymentId} could not be flagged as late; the next run retries it", candidate.Id);
            }
        }

        if (!emailsEnabled)
        {
            logger.LogInformation("Service payment reminders: online payments are switched off, no request or reminder is sent");
            return new ServicePaymentReminderRun(false, markedLate, 0, 0, skipped, errors);
        }

        // 2. The payments that were pending and can go out now: a catch-up for a supplier that became ready without anyone queueing
        //    the job (or whose job failed).
        var pending = await SendPendingAsync(null, cancellationToken);
        requestsSent += pending.Sent;
        skipped += pending.Skipped;
        errors += pending.Errors;

        // 3. The reminders that are due: at +2 and +7 days from the first request, three emails with a link in all, one a day at most.
        var days = options.Value.EffectiveReminderDays;
        var candidates = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.PaymentTokenHash != null && p.RequestedAt != null && p.SentCount >= 1
                        && p.SentCount < ServicePaymentLimits.MaxPaymentEmails
                        && (p.Status == ServicePaymentStatus.Requested || p.Status == ServicePaymentStatus.Failed))
            .Select(p => new { p.Id, p.ServiceRequestId, p.Status, p.SentCount, p.RequestedAt, p.LastSentAt })
            .ToListAsync(cancellationToken);

        var due = candidates
            .Where(c => ServicePaymentSchedule.ReminderIsDue(c.Status, c.SentCount, c.RequestedAt, c.LastSentAt, days, now))
            .OrderBy(c => c.RequestedAt)
            .Take(ServicePaymentLimits.ReminderBatchSize)
            .ToList();

        foreach (var candidate in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await IssueAndSendLinkAsync(candidate.Id, candidate.ServiceRequestId, LinkKind.Reminder, cancellationToken))
                {
                    case LinkOutcome.Sent:
                        remindersSent++;
                        break;
                    case LinkOutcome.Skipped:
                        skipped++;
                        break;
                    default:
                        errors++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Reminder of service payment {PaymentId} could not be sent; the next run retries it", candidate.Id);
            }
        }

        return new ServicePaymentReminderRun(true, markedLate, requestsSent, remindersSent, skipped, errors);
    }

    /// <summary>Flags a payment as late, once, under the lock (it may have been paid since). False when it is not late any more.</summary>
    private async Task<bool> MarkLateAsync(PaymentRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await UnderPaymentLockAsync(
            candidate.ServiceRequestId,
            async () =>
            {
                var payment = await LoadPaymentAsync(candidate.Id, cancellationToken);
                var now = Now();
                if (payment is null || payment.LateAt is not null
                    || !ServicePaymentSchedule.IsLate(payment.Status, payment.RequestedAt, options.Value.LateAfterDays, now))
                    return false;

                // The moment it became late, not the moment the job noticed: the same figure whenever the job runs.
                payment.LateAt = ServicePaymentSchedule.LateSince(payment.RequestedAt, options.Value.LateAfterDays);
                payment.UpdatedAt = now;
                logger.LogInformation("Service payment {PaymentId} is late since {LateAt:o}", payment.Id, payment.LateAt);
                return true;
            },
            cancellationToken);
    }

    // ─── SendPendingPaymentRequestsJob ──────────────────────────────────────────────────────────────────────────────

    public async Task<int> SendPendingRequestsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var run = await SendPendingAsync(supplierOrgId, cancellationToken);
        if (run.Sent + run.Errors > 0)
        {
            logger.LogInformation(
                "Pending payment requests of supplier {SupplierOrgId}: {Sent} sent, {Skipped} skipped, {Errors} failed",
                supplierOrgId, run.Sent, run.Skipped, run.Errors);
        }

        return run.Sent;
    }

    /// <summary>Sends the first request of the pending payments (those of one supplier, or of all when <paramref name="supplierOrgId"/> is null). Nothing with the flag off.</summary>
    private async Task<(int Sent, int Skipped, int Errors)> SendPendingAsync(Guid? supplierOrgId, CancellationToken cancellationToken)
    {
        if (!features.IsEnabled(FeatureFlags.SupplierOnlinePayments))
        {
            logger.LogInformation("Pending payment requests are not sent: online payments are switched off");
            return (0, 0, 0);
        }

        var query = db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.Status == ServicePaymentStatus.Requested && p.PaymentTokenHash == null && p.SentCount == 0);
        if (supplierOrgId is { } org)
            query = query.Where(p => p.SupplierOrgId == org);

        var pending = await query
            .OrderBy(p => p.CreatedAt)
            .Take(ServicePaymentLimits.ReminderBatchSize)
            .Select(p => new PaymentRef(p.Id, p.ServiceRequestId))
            .ToListAsync(cancellationToken);

        int sent = 0, skipped = 0, errors = 0;
        foreach (var candidate in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await IssueAndSendLinkAsync(candidate.Id, candidate.ServiceRequestId, LinkKind.FirstRequest, cancellationToken))
                {
                    case LinkOutcome.Sent:
                        sent++;
                        break;
                    case LinkOutcome.Skipped:
                        skipped++;
                        break;
                    default:
                        errors++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Payment request of service payment {PaymentId} could not be sent; the next run retries it", candidate.Id);
            }
        }

        return (sent, skipped, errors);
    }

    // ─── One link, one email ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a new link for a payment and emails it, under the payment lock, only if the payment is still due for it when the lock
    /// is held (it may have been paid, asked for or reminded meanwhile). The link is committed before the email is queued; if the
    /// email cannot be queued the link is taken back, so the payer's previous link keeps working and the next run tries again.
    /// </summary>
    private async Task<LinkOutcome> IssueAndSendLinkAsync(Guid paymentId, Guid requestId, LinkKind kind, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ServiceRequestPayment payment;
        string token;
        string? previousHash;
        DateTime? previousRequestedAt;
        DateTime? previousSentAt;
        int previousSentCount;
        await using (var transaction = await LockAsync(requestId, cancellationToken))
        {
            var loaded = await LoadPaymentAsync(paymentId, cancellationToken);
            var now = Now();
            if (loaded is null || !await IsLinkDueAsync(loaded, kind, now, cancellationToken))
                return LinkOutcome.Skipped;

            payment = loaded;
            previousHash = payment.PaymentTokenHash;
            previousRequestedAt = payment.RequestedAt;
            previousSentAt = payment.LastSentAt;
            previousSentCount = payment.SentCount;
            token = IssueToken(payment, now);
            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        // The new link is committed: sending it, or taking it back, does not depend on the job still being there.
        var queued = false;
        try
        {
            queued = kind == LinkKind.Retry
                ? await QueueRetryEmailAsync(payment, token, CancellationToken.None)
                : await QueueLinkEmailAsync(payment, token, reminder: kind == LinkKind.Reminder, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Email of payment {PaymentId} could not be prepared", payment.Id);
        }

        if (!queued)
        {
            RevokeLink(payment, previousHash, previousRequestedAt, previousSentAt, previousSentCount, Now());
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Service payment {PaymentId}: the {Kind} email could not be queued, the link was taken back", payment.Id, kind);
            return LinkOutcome.NotQueued;
        }

        logger.LogInformation("Service payment {PaymentId} of request {RequestId}: {Kind} queued ({SentCount} sent)", payment.Id, requestId, kind, payment.SentCount);
        return LinkOutcome.Sent;
    }

    /// <summary>
    /// The payment is due for the email of <paramref name="kind"/> now, with everything read again under the lock: the payer still owes
    /// it, the request is a completed one paid inside CasaZen with nothing left to confirm, the supplier can be paid, the payer has an
    /// address and, for a request or a reminder, the flag is on and (for a reminder) its day has come.
    /// </summary>
    private async Task<bool> IsLinkDueAsync(ServiceRequestPayment payment, LinkKind kind, DateTime now, CancellationToken cancellationToken)
    {
        if (kind == LinkKind.Retry ? payment.Status != ServicePaymentStatus.Failed : !ServicePaymentSchedule.IsOwed(payment.Status))
            return false;

        if (kind != LinkKind.Retry && !features.IsEnabled(FeatureFlags.SupplierOnlinePayments))
            return false;

        if (kind == LinkKind.FirstRequest && (payment.PaymentTokenHash is not null || payment.SentCount != 0))
            return false;

        if (kind == LinkKind.Reminder
            && !ServicePaymentSchedule.ReminderIsDue(
                payment.Status, payment.SentCount, payment.RequestedAt, payment.LastSentAt, options.Value.EffectiveReminderDays, now))
            return false;

        // The request has two parties and is not tenant-filtered; scoped by the id the payment points to.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == payment.ServiceRequestId)
            .Select(r => new { r.Status, r.PaymentMode, r.FinalAmountNeedsConfirmation })
            .FirstOrDefaultAsync(cancellationToken);
        if (request is not { Status: ServiceRequestStatus.Completato, PaymentMode: ServiceRequestPaymentMode.Online, FinalAmountNeedsConfirmation: false })
            return false;

        if (!(await ReadSupplierAsync(payment.SupplierOrgId, cancellationToken)).CanReceivePayments)
            return false;

        return !string.IsNullOrWhiteSpace(await ReadPayerEmailAsync(payment.PayerOrgId, cancellationToken));
    }

    /// <summary>Queues the email of a payment that failed after it was accepted, with its new link; false when it is not queued.</summary>
    private async Task<bool> QueueRetryEmailAsync(ServiceRequestPayment payment, string token, CancellationToken cancellationToken)
    {
        var recipient = await ReadPayerEmailAsync(payment.PayerOrgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
            return false;

        var context = await ReadContextAsync(payment, cancellationToken);
        var validUntil = ValidUntil(payment) ?? Now().AddDays(options.Value.PaymentLinkValidityDays);
        return emailQueue.Enqueue(
            recipient,
            EmailTemplates.ServicePaymentFailed(
                EmailTemplates.DefaultCulture, context.SupplierName, context.ServiceName, context.PropertyName, payment.AmountCents,
                validUntil, links.ServicePayment(payment.Id, token)),
            EmailTemplates.Names.ServicePaymentFailed);
    }
}
