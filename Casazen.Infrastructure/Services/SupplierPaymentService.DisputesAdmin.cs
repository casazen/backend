using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The platform admins' side of the disputes (SP-15c-c, decision VT-D16): the work queue, one dispute with the words of both parties, and the decision.
/// <c>AdminOnly</c> at the controller; every statement here is a read-only projection across the suppliers, on purpose, or is made under the payment lock.
/// </summary>
/// <remarks>
/// <para><b>The decision is one transaction under the payment lock</b> (the lock of the request, the one the release and the refunds take): it reads the
/// dispute and the payment again, checks that nothing changed, and writes the outcome. For the supplier the payment goes back to <c>Held</c>, due now. For
/// the customer the refund of the amount decided (1 to the price minus the commission minus what was refunded: <see cref="ServiceDisputeRules.RefundableToPayer"/>)
/// is <b>reserved in the same transaction</b>, so neither a release nor another refund can take its euros, and the payment stays <c>Disputed</c>; Stripe is asked
/// <i>after</i> the commit, with the idempotency key of the reserved row, and the payment leaves <c>Disputed</c> only when the refund has succeeded
/// (<see cref="SettleDecidedDisputeAsync"/>). The commission is never given back by a dispute (rule R4): it is CasaZen's, and it is the supplier who loses.</para>
/// <para><b>Idempotent, and a race is a 409.</b> The same decision on a decided dispute answers the same and does nothing (nothing is refunded twice, nothing
/// is penalized twice); a different decision, or one that lost the race to the payer's withdrawal, is 409 <c>service_dispute_state_changed</c>.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    /// <summary>One line of the admin's list, read in a single statement with the numbers of its payment.</summary>
    private sealed record DisputeRow(
        Guid Id,
        Guid ServiceRequestId,
        Guid PaymentId,
        Guid SupplierOrgId,
        ServiceDisputeStatus Status,
        ServiceDisputeReason Reason,
        ServiceDisputeOpener OpenedBy,
        bool NoShow,
        DateTime OpenedAt,
        DateTime SupplierReplyDueAt,
        DateTime? SupplierRepliedAt,
        DateTime? DecisionDueAt,
        DateTime? ResolvedAt,
        int? DecidedRefundCents,
        DateTime? SettledAt,
        int AmountCents,
        int NetCents,
        int RefundedCents);

    // ─── The work queue ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<AdminServiceDisputeSummary> Items, int TotalCount)> ListDisputesAsync(
        AdminServiceDisputeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<ServicePaymentDispute> disputes = db.ServicePaymentDisputes.AsNoTracking();
        if (query.Status is { } status)
            disputes = disputes.Where(d => d.Status == status);
        if (query.SupplierOrgId is { } supplierOrgId)
            disputes = disputes.Where(d => d.SupplierOrgId == supplierOrgId);
        if (query.OpenOnly)
            disputes = disputes.Where(d => d.Status == ServiceDisputeStatus.AwaitingSupplier || d.Status == ServiceDisputeStatus.AwaitingDecision);

        var total = await disputes.CountAsync(cancellationToken);

        // The work queue is read oldest first, so nothing waits behind newer cases; the history is read newest first.
        var ordered = query.OpenOnly
            ? disputes.OrderBy(d => d.OpenedAt).ThenBy(d => d.Id)
            : disputes.OrderByDescending(d => d.OpenedAt).ThenBy(d => d.Id);
        var rows = await ordered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(d => new DisputeRow(
                d.Id,
                d.ServiceRequestId,
                d.ServiceRequestPaymentId,
                d.SupplierOrgId,
                d.Status,
                d.Reason,
                d.OpenedBy,
                d.NoShow,
                d.OpenedAt,
                d.SupplierReplyDueAt,
                d.SupplierRepliedAt,
                d.DecisionDueAt,
                d.ResolvedAt,
                d.DecidedRefundCents,
                d.SettledAt,
                d.ServiceRequestPayment.AmountCents,
                d.ServiceRequestPayment.NetCents,
                d.ServiceRequestPayment.RefundedCents))
            .ToListAsync(cancellationToken);

        // The supplier's name, for the line: the org is the supplier itself, read by id.
        var supplierIds = rows.Select(r => r.SupplierOrgId).Distinct().ToList();
        var names = await db.Orgs
            .AsNoTracking()
            .Where(o => supplierIds.Contains(o.Id))
            .Select(o => new { o.Id, o.DisplayName, o.Name })
            .ToListAsync(cancellationToken);

        var now = Now();
        var items = rows
            .Select(r =>
            {
                var org = names.FirstOrDefault(n => n.Id == r.SupplierOrgId);
                var name = org is null ? string.Empty : string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName;
                return ToAdminSummary(r, name, ServiceDisputeRules.RefundableToPayer(r.NetCents, r.RefundedCents, 0), now);
            })
            .ToList();
        return (items, total);
    }

    public async Task<AdminServiceDisputeDetail> GetDisputeAsync(Guid disputeId, CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        return await AdminDetailAsync(disputeId, cancellationToken);
    }

    /// <summary>One dispute as the admin who decides reads it: the line of the queue, the state of the money and the words of both parties.</summary>
    private async Task<AdminServiceDisputeDetail> AdminDetailAsync(Guid disputeId, CancellationToken cancellationToken)
    {
        var dispute = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Id == disputeId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ServiceDisputeErrors.DisputeNotFound();

        var payment = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Id == disputeId)
            .Select(d => new
            {
                d.ServiceRequestPayment.Status,
                d.ServiceRequestPayment.AmountCents,
                d.ServiceRequestPayment.ApplicationFeeCents,
                d.ServiceRequestPayment.NetCents,
                d.ServiceRequestPayment.RefundedCents,
            })
            .FirstAsync(cancellationToken);

        // What can still go back to the payer counts the refunds that are on their way, not only the ones that succeeded.
        var refunds = await LoadRefundsAsync(dispute.ServiceRequestPaymentId, cancellationToken);
        var inProgress = (int)Math.Min(refunds.Where(IsInProgress).Sum(r => (long)r.AmountCents), int.MaxValue);

        // The request has two parties and is not tenant-filtered; scoped by the id the dispute points to.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == dispute.ServiceRequestId)
            .Select(r => new { r.Status, r.ScheduledEndUtc, r.ServiceNameSnapshot, r.Category })
            .FirstOrDefaultAsync(cancellationToken);

        var supplier = await ReadSupplierAsync(dispute.SupplierOrgId, cancellationToken);
        var row = new DisputeRow(
            dispute.Id,
            dispute.ServiceRequestId,
            dispute.ServiceRequestPaymentId,
            dispute.SupplierOrgId,
            dispute.Status,
            dispute.Reason,
            dispute.OpenedBy,
            dispute.NoShow,
            dispute.OpenedAt,
            dispute.SupplierReplyDueAt,
            dispute.SupplierRepliedAt,
            dispute.DecisionDueAt,
            dispute.ResolvedAt,
            dispute.DecidedRefundCents,
            dispute.SettledAt,
            payment.AmountCents,
            payment.NetCents,
            payment.RefundedCents);

        return new AdminServiceDisputeDetail(
            ToAdminSummary(row, supplier.Name, ServiceDisputeRules.RefundableToPayer(payment.NetCents, payment.RefundedCents, inProgress), Now()),
            payment.Status,
            payment.ApplicationFeeCents,
            !string.IsNullOrWhiteSpace(request?.ServiceNameSnapshot)
                ? request.ServiceNameSnapshot
                : EmailTemplates.ServiceCategoryLabel(EmailTemplates.DefaultCulture, request?.Category ?? string.Empty),
            request?.Status ?? ServiceRequestStatus.Annullato,
            request?.ScheduledEndUtc,
            dispute.CustomerText,
            dispute.SupplierReply,
            dispute.AdminNote,
            dispute.ResolvedByUserId);
    }

    private static AdminServiceDisputeSummary ToAdminSummary(DisputeRow r, string supplierName, int refundableCents, DateTime now) => new(
        r.Id,
        r.ServiceRequestId,
        r.PaymentId,
        r.SupplierOrgId,
        supplierName,
        r.Status,
        r.Reason,
        r.OpenedBy,
        r.NoShow,
        r.OpenedAt,
        r.SupplierReplyDueAt,
        r.SupplierRepliedAt,
        r.DecisionDueAt,
        Overdue: r.Status == ServiceDisputeStatus.AwaitingDecision && r.DecisionDueAt is { } due && now >= due,
        r.ResolvedAt,
        r.AmountCents,
        r.NetCents,
        r.RefundedCents,
        refundableCents,
        r.DecidedRefundCents,
        Settled: r.Status == ServiceDisputeStatus.ResolvedForCustomer && r.SettledAt is not null);

    // ─── The decision ────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<AdminServiceDisputeDetail> ResolveDisputeAsync(
        Guid disputeId,
        ResolveServiceDisputeRequest request,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        // What is asked is checked before anything is locked.
        if (!ServiceDisputeRules.TryParseOutcome(request.Outcome, out var outcome))
            throw ServiceDisputeErrors.OutcomeNotValid();
        if (outcome == ServiceDisputeStatus.ResolvedForSupplier && request.RefundCents is not null)
            throw ServiceDisputeErrors.InvalidFields(ServiceDisputeLimits.RefundField);

        var note = ServiceDisputeRules.NormalizeText(request.Note, required: false, ServiceDisputeLimits.NoteField);

        var key = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.Id == disputeId)
            .Select(d => new { d.ServiceRequestId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ServiceDisputeErrors.DisputeNotFound();

        db.ChangeTracker.Clear();
        ServiceRequestPaymentRefund? reserved = null;
        ServicePaymentDispute dispute;
        var now = Now();
        await using (var transaction = await LockAsync(key.ServiceRequestId, cancellationToken))
        {
            dispute = await db.ServicePaymentDisputes.Where(d => d.Id == disputeId).FirstOrDefaultAsync(cancellationToken)
                      ?? throw ServiceDisputeErrors.DisputeNotFound();

            // Decided already: the same decision is the same answer and moves nothing; any other is a conflict (another admin, the payer's withdrawal).
            if (ServiceDisputeRules.IsResolved(dispute.Status))
            {
                if (dispute.Status == outcome && SameDecision(dispute, request))
                    return await AdminDetailAsync(dispute.Id, cancellationToken);

                throw ServiceDisputeErrors.DisputeChanged();
            }

            // The supplier has its time to reply; an admin who decides before that takes it away.
            if (!ServiceDisputeRules.CanResolve(dispute.Status))
                throw ServiceDisputeErrors.SupplierStillHasTime(dispute.SupplierReplyDueAt);

            var payment = await LoadPaymentAsync(dispute.ServiceRequestPaymentId, cancellationToken)
                          ?? throw ServiceDisputeErrors.DisputeNotFound();
            if (payment.FundsModel != ServiceFundsModel.PlatformHeld)
                throw ServiceDisputeErrors.PaymentNotDecidable();

            if (outcome == ServiceDisputeStatus.ResolvedForSupplier)
            {
                await ResumeSupplierPaymentAsync(payment, dispute, now, cancellationToken);
            }
            else
            {
                reserved = await DecideForCustomerAsync(payment, dispute, request.RefundCents, actorUserId, now, cancellationToken);
            }

            dispute.Status = outcome;
            dispute.ResolvedAt = now;
            dispute.ResolvedByUserId = Truncate(actorUserId, 255);
            dispute.AdminNote = note;
            dispute.UpdatedAt = now;

            db.SupplierAdminAuditEntries.Add(new SupplierAdminAuditEntry
            {
                Action = SupplierAdminAuditAction.ServiceDisputeResolved,
                SupplierOrgId = dispute.SupplierOrgId,
                ActorUserId = Truncate(actorUserId, 200)!,
                OccurredAt = now,
                Reason = Truncate(
                    $"dispute {dispute.Id:D} {(outcome == ServiceDisputeStatus.ResolvedForSupplier ? "for_supplier" : "for_customer")} refund {dispute.DecidedRefundCents ?? 0} cents",
                    500),
            });

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogWarning(
            "Dispute {DisputeId} of request {RequestId} decided by admin {ActorUserId}: {Outcome}, {RefundCents} cents to the payer",
            dispute.Id, dispute.ServiceRequestId, actorUserId, outcome, dispute.DecidedRefundCents ?? 0);

        // Stripe is asked after the commit, with the key of the row that was reserved with the decision. A failure here changes nothing that was
        // decided: the refund waits, the sync job sends it again, and the admins are told if it does not go through.
        if (reserved is not null)
        {
            try
            {
                await SubmitRefundAsync(reserved.Id, resubmission: false, throwOnTransient: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(
                    ex, "The refund {RefundId} decided by dispute {DisputeId} could not be sent now; the sync job sends it again", reserved.Id, dispute.Id);
            }
        }

        await CompleteAsync(
            [new ServicePaymentNotice(ServicePaymentNoticeKind.DisputeResolved, dispute.ServiceRequestPaymentId, DisputeId: dispute.Id)],
            CancellationToken.None);
        await RecordPenaltyAsync(dispute, outcome, now);

        db.ChangeTracker.Clear();
        return await AdminDetailAsync(dispute.Id, cancellationToken);
    }

    /// <summary>The decision asked for is the one already taken: the outcome was compared, the amount is left to compare (none asked means the one decided).</summary>
    private static bool SameDecision(ServicePaymentDispute dispute, ResolveServiceDisputeRequest request) =>
        dispute.Status != ServiceDisputeStatus.ResolvedForCustomer
        || request.RefundCents is null
        || request.RefundCents == dispute.DecidedRefundCents;

    /// <summary>
    /// The supplier was at fault: reserves, under the lock the caller holds, the refund of the amount decided — at most the price minus the commission
    /// minus what was refunded and what is being refunded — and records how much the payment must have given back for the decision to be done. The
    /// payment stays <see cref="ServicePaymentStatus.Disputed"/>. Returns the pending refund (the caller asks Stripe for it after the commit), or
    /// <c>null</c> when nothing is left to give back (the decision stands, the supplier is penalized all the same).
    /// </summary>
    private async Task<ServiceRequestPaymentRefund?> DecideForCustomerAsync(
        ServiceRequestPayment payment,
        ServicePaymentDispute dispute,
        int? requestedCents,
        string actorUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (payment.Status is not (ServicePaymentStatus.Disputed or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded))
        {
            logger.LogError(
                "Dispute {DisputeId}: payment {PaymentId} is {Status}, not a state a decision can be made on. An admin has to look at it",
                dispute.Id, payment.Id, payment.Status);
            throw ServiceDisputeErrors.PaymentNotDecidable();
        }

        var refunds = await LoadRefundsAsync(payment.Id, cancellationToken);
        var refunded = (int)Math.Min(refunds.Where(r => r.Status == ServicePaymentRefundStatus.Succeeded).Sum(r => (long)r.AmountCents), int.MaxValue);
        var inProgress = (int)Math.Min(refunds.Where(IsInProgress).Sum(r => (long)r.AmountCents), int.MaxValue);

        var decision = ServiceDisputeRules.DecideRefund(requestedCents, ServiceDisputeRules.RefundableToPayer(payment.NetCents, refunded, inProgress));
        switch (decision.Kind)
        {
            case DisputeRefundKind.Invalid:
                throw ServiceDisputeErrors.RefundAmountInvalid();
            case DisputeRefundKind.Exceeds:
                throw ServiceDisputeErrors.RefundAmountExceeds(decision.AmountCents);
        }

        var amount = decision.Kind == DisputeRefundKind.Refund ? decision.AmountCents : 0;
        dispute.DecidedRefundCents = amount;
        dispute.RefundTargetCents = ServiceDisputeRules.RefundTarget(refunded, inProgress, amount);

        // Nothing to give back and the payment already ended (everything but the commission was refunded some other way): nothing to wait for.
        if (amount == 0 && payment.Status != ServicePaymentStatus.Disputed)
            dispute.SettledAt = now;

        if (amount == 0)
            return null;

        // The euros are reserved now: no release (the payment is Disputed) and no other refund can take them; the commission is kept.
        var reservation = ReserveHeld(
            payment, refunds, amount, $"dispute:{dispute.Id:D}", ServicePaymentRefundOrigin.System, actorUserId, now, keepCommission: true);
        db.ServiceRequestPaymentRefunds.Add(reservation);
        return reservation;
    }

    /// <summary>
    /// Tells the ranking that the supplier lost (a dispute decided for the customer, or a no-show). A failure here is logged and never undoes the decision:
    /// the dispute row is the source from which the event can be rebuilt. Without a sink the penalty is only logged.
    /// </summary>
    private async Task RecordPenaltyAsync(ServicePaymentDispute dispute, ServiceDisputeStatus outcome, DateTime at)
    {
        if (ServiceDisputeRules.PenaltyFor(outcome, dispute.NoShow) is not { } kind)
            return;

        if (penalties is null)
        {
            logger.LogInformation(
                "Dispute {DisputeId}: supplier {SupplierOrgId} lost it ({Kind}); no ranking is connected yet, the dispute row is the record",
                dispute.Id, dispute.SupplierOrgId, kind);
            return;
        }

        try
        {
            await penalties.RecordAsync(
                new SupplierDisputePenalty(dispute.SupplierOrgId, dispute.Id, dispute.ServiceRequestId, kind, at), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex, "The penalty ({Kind}) of dispute {DisputeId} could not be recorded; it can be rebuilt from the dispute", kind, dispute.Id);
        }
    }
}
