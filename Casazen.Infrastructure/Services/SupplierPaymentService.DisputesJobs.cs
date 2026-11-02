using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The dispute step of the funds job (SP-15c-c, <see cref="IServiceDisputeJobService"/>, run by <c>service-funds</c> every five minutes): it sends to the
/// admins the disputes whose supplier did not reply in time, tells them when their target passed or when the refund of a decision did not go through,
/// and opens the complaint for a job nobody marked completed once the time agreed for it ended (the no-show, VT-D16).
/// </summary>
/// <remarks>
/// <para>It decides nothing about the money: the only thing it moves is the state of a dispute (<c>AwaitingSupplier</c> → <c>AwaitingDecision</c>) and, for a
/// no-show, the status of a payment from <c>Held</c> to <c>Disputed</c> — which only <i>stops</i> money. It never calls Stripe. Each dispute is handled by
/// the same methods as the request that would have handled it, under the payment lock of its request and read again after it, with the change tracker
/// cleared before it, so a run that overlaps another run, a reply or an admin's decision finds the dispute already changed and skips it. An error on one is
/// logged and counted, and the others go on. Always scheduled, whatever the state of the flag <c>SupplierOnlinePayments</c>.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    public async Task<ServiceDisputesRun> RunDisputesAsync(CancellationToken cancellationToken = default)
    {
        int reached = 0, overdue = 0, refundAlerts = 0, noShows = 0, errors = 0;
        var now = Now();

        // 1. The supplier did not reply in time: the dispute goes to the admins without its reply (a late reply is still taken).
        var waiting = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Status == ServiceDisputeStatus.AwaitingSupplier && d.SupplierReplyDueAt <= now)
            .OrderBy(d => d.SupplierReplyDueAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(d => new DisputeRef(d.Id, d.ServiceRequestId))
            .ToListAsync(cancellationToken);
        foreach (var candidate in waiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await ReachAdminsAsync(candidate, cancellationToken))
                    reached++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Dispute {DisputeId} could not be sent to the admins; the next run tries again", candidate.Id);
            }
        }

        // 2. The admins' target to decide has passed: they are told, once.
        var late = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Status == ServiceDisputeStatus.AwaitingDecision
                        && d.DecisionOverdueAlertedAt == null
                        && d.DecisionDueAt != null
                        && d.DecisionDueAt <= now)
            .OrderBy(d => d.DecisionDueAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(d => new DisputeRef(d.Id, d.ServiceRequestId))
            .ToListAsync(cancellationToken);
        foreach (var candidate in late)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await AlertOverdueAsync(candidate, cancellationToken))
                    overdue++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "The overdue alert of dispute {DisputeId} could not be recorded; the next run tries again", candidate.Id);
            }
        }

        // 3. A decision for the customer whose refund did not go through: the admins are told, once. The payment stays disputed (nothing is paid out).
        var alertBefore = now - TimeSpan.FromMinutes(options.Value.DisputeRefundAlertMinutes);
        var undone = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Status == ServiceDisputeStatus.ResolvedForCustomer
                        && d.SettledAt == null
                        && d.RefundAlertedAt == null
                        && d.ResolvedAt != null
                        && d.ResolvedAt <= alertBefore)
            .OrderBy(d => d.ResolvedAt)
            .Take(ServicePaymentLimits.SyncBatchSize)
            .Select(d => new DisputeRef(d.Id, d.ServiceRequestId))
            .ToListAsync(cancellationToken);
        foreach (var candidate in undone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await AlertRefundIncompleteAsync(candidate, cancellationToken))
                    refundAlerts++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "The refund of dispute {DisputeId} could not be checked; the next run tries again", candidate.Id);
            }
        }

        // 4. A job nobody marked completed, whose time ended long enough ago: CasaZen opens the complaint for the payer.
        var cutoff = now - options.Value.DisputeNoShow;
        var missed = await (
                from p in db.ServiceRequestPayments
                    .AsNoTracking()
                    .Where(p => p.FundsModel == ServiceFundsModel.PlatformHeld && p.Status == ServicePaymentStatus.Held)
                join r in db.ServiceRequests.IgnoreQueryFilters().AsNoTracking() on p.ServiceRequestId equals r.Id
                where (r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso)
                      && r.ScheduledEndUtc != null
                      && r.ScheduledEndUtc <= cutoff
                      && !db.ServicePaymentDisputes.Where(d => d.ServiceRequestPaymentId == p.Id).Any()
                orderby r.ScheduledEndUtc
                select new PaymentRef(p.Id, p.ServiceRequestId))
            .Take(ServicePaymentLimits.SyncBatchSize)
            .ToListAsync(cancellationToken);
        foreach (var candidate in missed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var opened = await OpenCoreAsync(
                    new DisputeOpening(
                        candidate.ServiceRequestId,
                        ServiceDisputeOpener.System,
                        ServiceDisputeReason.NotDone,
                        Text: null,
                        ActorOrgId: null,
                        ActorUserId: null,
                        BySystem: true),
                    cancellationToken);
                if (opened is not null)
                    noShows++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "The no-show complaint of request {RequestId} could not be opened; the next run tries again", candidate.ServiceRequestId);
            }
        }

        return new ServiceDisputesRun(reached, overdue, refundAlerts, noShows, errors);
    }

    /// <summary>The supplier's time to reply passed: the dispute reaches the admins, who are told. Under the lock, read again.</summary>
    private async Task<bool> ReachAdminsAsync(DisputeRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ServicePaymentNotice? notice = null;
        var done = await UnderPaymentLockAsync(
            candidate.ServiceRequestId,
            async () =>
            {
                var dispute = await db.ServicePaymentDisputes.Where(d => d.Id == candidate.Id).FirstOrDefaultAsync(cancellationToken);
                var now = Now();
                if (dispute is null || !ServiceDisputeRules.ReplyTermPassed(dispute.Status, dispute.SupplierReplyDueAt, now))
                    return false;

                dispute.Status = ServiceDisputeStatus.AwaitingDecision;
                dispute.DecisionDueAt = ServiceDisputeRules.DecisionDueAt(now, options.Value.DisputeDecisionTarget);
                dispute.UpdatedAt = now;
                notice = new ServicePaymentNotice(
                    ServicePaymentNoticeKind.DisputeAdminAlert, dispute.ServiceRequestPaymentId, Detail: DisputeAlertCodes.NoReply, DisputeId: dispute.Id);
                logger.LogInformation("Dispute {DisputeId}: the supplier did not reply in time, it goes to the admins", dispute.Id);
                return true;
            },
            cancellationToken);

        if (notice is not null)
            await CompleteAsync([notice], CancellationToken.None);

        return done;
    }

    /// <summary>The admins' target to decide passed: records it and tells them, once. Under the lock, read again.</summary>
    private async Task<bool> AlertOverdueAsync(DisputeRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ServicePaymentNotice? notice = null;
        var done = await UnderPaymentLockAsync(
            candidate.ServiceRequestId,
            async () =>
            {
                var dispute = await db.ServicePaymentDisputes.Where(d => d.Id == candidate.Id).FirstOrDefaultAsync(cancellationToken);
                var now = Now();
                if (dispute is null
                    || !ServiceDisputeRules.DecisionOverdue(dispute.Status, dispute.DecisionDueAt, dispute.DecisionOverdueAlertedAt, now))
                    return false;

                dispute.DecisionOverdueAlertedAt = now;
                dispute.UpdatedAt = now;
                notice = new ServicePaymentNotice(
                    ServicePaymentNoticeKind.DisputeAdminAlert, dispute.ServiceRequestPaymentId, Detail: DisputeAlertCodes.Overdue, DisputeId: dispute.Id);
                logger.LogError("Dispute {DisputeId} has waited for a decision longer than the target: an admin has to decide it", dispute.Id);
                return true;
            },
            cancellationToken);

        if (notice is not null)
            await CompleteAsync([notice], CancellationToken.None);

        return done;
    }

    /// <summary>
    /// The refund of a decision for the customer has not gone through (Stripe refused it, or it was never sent and none is waiting): records it and tells the
    /// admins, once. A decision whose payment has in the meantime given back what it asked for is settled here (the safety net of the hook that runs with the
    /// refund) and not alerted. Under the lock, read again.
    /// </summary>
    private async Task<bool> AlertRefundIncompleteAsync(DisputeRef candidate, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ServicePaymentNotice? notice = null;
        var done = await UnderPaymentLockAsync(
            candidate.ServiceRequestId,
            async () =>
            {
                var dispute = await db.ServicePaymentDisputes.Where(d => d.Id == candidate.Id).FirstOrDefaultAsync(cancellationToken);
                if (dispute is not { Status: ServiceDisputeStatus.ResolvedForCustomer, SettledAt: null, RefundAlertedAt: null })
                    return false;

                var payment = await LoadPaymentAsync(dispute.ServiceRequestPaymentId, cancellationToken);
                if (payment is null)
                    return false;

                var now = Now();
                if (dispute.RefundTargetCents is { } target && ServiceDisputeRules.RefundDone(payment.RefundedCents, target))
                {
                    await SettleDecidedDisputeAsync(payment, now, cancellationToken);
                    return false;
                }

                var refunds = await LoadRefundsAsync(payment.Id, cancellationToken);
                var inProgress = (int)Math.Min(refunds.Where(IsInProgress).Sum(r => (long)r.AmountCents), int.MaxValue);
                if (!ServiceDisputeRules.RefundAlertDue(
                        dispute.Status,
                        dispute.ResolvedAt,
                        dispute.SettledAt,
                        dispute.RefundAlertedAt,
                        payment.RefundedCents,
                        dispute.RefundTargetCents ?? 0,
                        inProgress,
                        TimeSpan.FromMinutes(options.Value.DisputeRefundAlertMinutes),
                        now))
                    return false;

                dispute.RefundAlertedAt = now;
                dispute.UpdatedAt = now;
                notice = new ServicePaymentNotice(
                    ServicePaymentNoticeKind.DisputeAdminAlert, payment.Id, Detail: DisputeAlertCodes.RefundIncomplete, DisputeId: dispute.Id);
                logger.LogError(
                    "Dispute {DisputeId} was decided for the customer but its refund did not go through; payment {PaymentId} stays disputed. An admin has to look at it",
                    dispute.Id, payment.Id);
                return true;
            },
            cancellationToken);

        if (notice is not null)
            await CompleteAsync([notice], CancellationToken.None);

        return done;
    }
}
