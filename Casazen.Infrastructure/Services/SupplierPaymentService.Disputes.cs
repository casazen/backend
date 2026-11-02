using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The complaint about a job paid through CasaZen (SP-15c-c, <see cref="IServiceDisputeService"/>): the payer reports a problem, the money CasaZen
/// holds stops, the supplier replies, the payer may take the complaint back. The decision of the admins is in <c>.DisputesAdmin.cs</c>, the job in
/// <c>.DisputesJobs.cs</c>, the e-mails in <c>.DisputesNotices.cs</c>.
/// </summary>
/// <remarks>
/// <para><b>The complaint stops the money by the status, under the lock.</b> Opening takes the payment lock of the request (the one the release and the
/// refunds take), reads the payment again, checks the window (<see cref="ServiceDisputeRules.CanReport"/>) and, in the same transaction, writes the dispute
/// and moves the payment <see cref="ServicePaymentStatus.Held"/> → <see cref="ServicePaymentStatus.Disputed"/>. The release decides from the status
/// (<see cref="HeldPaymentRules.Release"/> refuses anything but <c>Held</c> and <c>TransferFailed</c>), so whichever takes the lock first wins: a release
/// that was first makes the complaint a 422 <c>service_dispute_window_closed</c>, a complaint that was first makes the release wait. Nothing is sent to
/// Stripe by a complaint, a reply or a withdrawal.</para>
/// <para><b>Idempotent.</b> A second complaint on the same payment is the same dispute and writes nothing; the same reply twice is one reply; a
/// withdrawal of a withdrawn complaint answers the same. Every state change is made under the lock on the dispute read again after it.</para>
/// <para><b>No words in a log.</b> The texts of the parties are encrypted at rest and returned only to the audience the view was made for; logs carry ids
/// and codes only.</para>
/// </remarks>
public sealed partial class SupplierPaymentService
{
    /// <summary>What a payer asks when it complains (or what the job asks on its behalf): the same checks for both, the job's stricter.</summary>
    private sealed record DisputeOpening(
        Guid RequestId,
        ServiceDisputeOpener OpenedBy,
        ServiceDisputeReason Reason,
        string? Text,
        Guid? ActorOrgId,
        string? ActorUserId,
        bool BySystem);

    /// <summary>A dispute found by the job before the lock: enough to take the lock of its request.</summary>
    private sealed record DisputeRef(Guid Id, Guid ServiceRequestId);

    // ─── The payer's complaint ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceDisputePayerView> OpenDisputeAsync(OpenServiceDisputeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OpenedBy == ServiceDisputeOpener.System)
            throw new ArgumentException("The complaint of a no-show is opened by the job, not through this call.", nameof(request));

        if (!Enum.IsDefined(request.Reason))
            throw ServiceDisputeErrors.InvalidFields(ServiceDisputeLimits.ReasonField);

        // The words are checked before anything is locked: a text that is too long or has control characters never reaches the database.
        var text = ServiceDisputeRules.NormalizeText(
            request.Text, required: request.Reason == ServiceDisputeReason.Other, ServiceDisputeLimits.TextField);

        var opened = await OpenCoreAsync(
            new DisputeOpening(request.ServiceRequestId, request.OpenedBy, request.Reason, text, request.ActorOrgId, request.ActorUserId, BySystem: false),
            cancellationToken);
        return ToPayerView(opened!);
    }

    /// <summary>
    /// Opens the complaint under the lock, or answers the one that is open. For the job (<see cref="DisputeOpening.BySystem"/>) it opens only the
    /// no-show whose time has come and answers <c>null</c> for everything else instead of refusing it: a job has nobody to tell.
    /// </summary>
    private async Task<ServicePaymentDispute?> OpenCoreAsync(DisputeOpening opening, CancellationToken cancellationToken)
    {
        var latest = await FindLatestHeldAsync(opening.RequestId, cancellationToken);
        if (latest is not { } known || known.Status == ServicePaymentStatus.Canceled)
            return opening.BySystem ? null : throw ServiceDisputeErrors.CannotReport();

        db.ChangeTracker.Clear();
        ServicePaymentDispute dispute;
        await using (var transaction = await LockAsync(opening.RequestId, cancellationToken))
        {
            var payment = await LoadPaymentAsync(known.Id, cancellationToken);
            if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld })
                return opening.BySystem ? null : throw ServiceDisputeErrors.CannotReport();

            EnsureOwnPayment(payment, opening.OpenedBy, opening.ActorOrgId);

            var existing = await OpenDisputeOfAsync(payment.Id, cancellationToken);
            var facts = await ReportFactsAsync(payment, existing is not null, cancellationToken);
            var now = Now();

            switch (ServiceDisputeRules.CanReport(facts, now))
            {
                case DisputeReportVerdict.AlreadyOpen:
                    // The same complaint: nothing is written. The job has nothing to do either.
                    return opening.BySystem ? null : existing;

                case DisputeReportVerdict.AllowedNoShow:
                    // CasaZen itself waits for the no-show time after the end of the work; the payer does not.
                    if (opening.BySystem
                        && (facts.ScheduledEndUtc is not { } end || now < ServiceDisputeRules.NoShowOpensAt(end, options.Value.DisputeNoShow)))
                    {
                        return null;
                    }

                    break;

                case DisputeReportVerdict.Allowed:
                    // A completed job is not a no-show: the job never complains about it.
                    if (opening.BySystem)
                        return null;

                    break;

                case DisputeReportVerdict.WindowClosed:
                    return opening.BySystem
                        ? null
                        : throw ServiceDisputeErrors.ReportWindowClosed(payment.ReleaseConfirmedAt ?? payment.ReleasedAt ?? payment.ReleaseAt);

                case DisputeReportVerdict.TooEarly:
                    return opening.BySystem ? null : throw ServiceDisputeErrors.ReportTooEarly();

                default:
                    return opening.BySystem ? null : throw ServiceDisputeErrors.CannotReport();
            }

            dispute = new ServicePaymentDispute
            {
                ServiceRequestPaymentId = payment.Id,
                ServiceRequestId = payment.ServiceRequestId,
                SupplierOrgId = payment.SupplierOrgId,
                Status = ServiceDisputeStatus.AwaitingSupplier,
                Reason = opening.Reason,
                OpenedBy = opening.OpenedBy,
                OpenedByUserId = Truncate(opening.ActorUserId, 255),
                NoShow = facts.RequestStatus is not (ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato),
                CustomerText = opening.Text,
                OpenedAt = now,
                SupplierReplyDueAt = ServiceDisputeRules.SupplierReplyDueAt(now, options.Value.DisputeSupplierReply),
                UpdatedAt = now,
            };
            db.ServicePaymentDisputes.Add(dispute);

            // The money stops here: the release refuses a payment that is not Held (or TransferFailed), and the refund of a decision needs it Disputed.
            payment.Status = ServicePaymentStatus.Disputed;
            payment.UpdatedAt = now;

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation(
            "Dispute {DisputeId} opened on payment {PaymentId} of request {RequestId} by {OpenedBy} ({Reason}, no-show {NoShow})",
            dispute.Id, dispute.ServiceRequestPaymentId, dispute.ServiceRequestId, dispute.OpenedBy, dispute.Reason, dispute.NoShow);

        await CompleteAsync(
            [new ServicePaymentNotice(ServicePaymentNoticeKind.DisputeOpened, dispute.ServiceRequestPaymentId, DisputeId: dispute.Id)],
            CancellationToken.None);
        return dispute;
    }

    /// <summary>
    /// The payer who complains is the payer of this payment: the private customer of a showcase for a payment with no org, the host that paid for the
    /// payment of a host. Anyone else gets what a stranger gets: nothing is found.
    /// </summary>
    private static void EnsureOwnPayment(ServiceRequestPayment payment, ServiceDisputeOpener party, Guid? hostOrgId)
    {
        var own = party switch
        {
            ServiceDisputeOpener.Customer => payment.PayerKind == ServicePayerKind.Private && payment.PayerOrgId is null,
            ServiceDisputeOpener.Host => payment.PayerKind == ServicePayerKind.Host && payment.PayerOrgId is { } payer && payer == hostOrgId,
            _ => true,
        };

        if (!own)
            throw ServiceDisputeErrors.DisputeNotFound();
    }

    /// <summary>What <see cref="ServiceDisputeRules.CanReport"/> looks at, read under the lock: the payment as it is and the request's status and schedule.</summary>
    private async Task<DisputeReportFacts> ReportFactsAsync(ServiceRequestPayment payment, bool hasOpenDispute, CancellationToken cancellationToken)
    {
        // The request has two parties and is not tenant-filtered; scoped by the id the payment points to.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == payment.ServiceRequestId)
            .Select(r => new { r.Status, r.ScheduledEndUtc })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ServiceDisputeErrors.CannotReport();

        return new DisputeReportFacts(
            payment.Status, payment.ReleaseAt, payment.ReleaseConfirmedAt, request.Status, request.ScheduledEndUtc, hasOpenDispute);
    }

    /// <summary>The open dispute of the payment, tracked, or null. At most one exists (a unique partial index).</summary>
    private async Task<ServicePaymentDispute?> OpenDisputeOfAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.ServicePaymentDisputes
            .Where(d => d.ServiceRequestPaymentId == paymentId
                        && (d.Status == ServiceDisputeStatus.AwaitingSupplier || d.Status == ServiceDisputeStatus.AwaitingDecision))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The latest dispute of the payment, tracked, whatever its state, or null.</summary>
    private async Task<ServicePaymentDispute?> LatestDisputeOfAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.ServicePaymentDisputes
            .Where(d => d.ServiceRequestPaymentId == paymentId)
            .OrderByDescending(d => d.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

    // ─── The payer takes the complaint back ──────────────────────────────────────────────────────────────────────────

    public async Task<ServiceDisputePayerView> WithdrawDisputeAsync(
        Guid serviceRequestId,
        ServiceDisputeOpener party,
        Guid? hostOrgId,
        CancellationToken cancellationToken = default)
    {
        if (party == ServiceDisputeOpener.System)
            throw new ArgumentException("Only the payer takes a complaint back.", nameof(party));

        var latest = await FindLatestHeldAsync(serviceRequestId, cancellationToken);
        if (latest is not { } known)
            throw ServiceDisputeErrors.DisputeNotFound();

        db.ChangeTracker.Clear();
        ServicePaymentDispute dispute;
        await using (var transaction = await LockAsync(serviceRequestId, cancellationToken))
        {
            var payment = await LoadPaymentAsync(known.Id, cancellationToken);
            if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld })
                throw ServiceDisputeErrors.DisputeNotFound();

            EnsureOwnPayment(payment, party, hostOrgId);

            dispute = await LatestDisputeOfAsync(payment.Id, cancellationToken) ?? throw ServiceDisputeErrors.DisputeNotFound();
            if (dispute.Status == ServiceDisputeStatus.Withdrawn)
                return ToPayerView(dispute);

            if (!ServiceDisputeRules.CanWithdraw(dispute.Status))
                throw ServiceDisputeErrors.DisputeNotOpen();

            // The payer says it is fine: the supplier is paid as if nothing had happened, at the next run of the funds job.
            var now = Now();
            await ResumeSupplierPaymentAsync(payment, dispute, now, cancellationToken);
            dispute.Status = ServiceDisputeStatus.Withdrawn;
            dispute.ResolvedAt = now;
            dispute.UpdatedAt = now;

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation(
            "Dispute {DisputeId} of request {RequestId} was withdrawn by the payer", dispute.Id, dispute.ServiceRequestId);

        await CompleteAsync(
            [new ServicePaymentNotice(ServicePaymentNoticeKind.DisputeWithdrawn, dispute.ServiceRequestPaymentId, DisputeId: dispute.Id)],
            CancellationToken.None);
        return ToPayerView(dispute);
    }

    /// <summary>
    /// The complaint is over in the supplier's favour (the admin decided so, or the payer took it back): the payment is <c>Held</c> again and due now,
    /// so the funds job pays the supplier at its next run, and a job nobody marked completed is completed — the work was done. A payment whose money
    /// was all given back by other means has nothing to pay and is left as it is. Any other state of the payment is not one a decision can be made on.
    /// </summary>
    private async Task ResumeSupplierPaymentAsync(ServiceRequestPayment payment, ServicePaymentDispute dispute, DateTime now, CancellationToken cancellationToken)
    {
        switch (payment.Status)
        {
            case ServicePaymentStatus.Disputed:
                payment.Status = ServicePaymentStatus.Held;
                payment.ReleaseAt = now;
                payment.UpdatedAt = now;
                break;

            case ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded:
                return;

            default:
                logger.LogError(
                    "Dispute {DisputeId}: payment {PaymentId} is {Status}, not a state a dispute can be closed in. An admin has to look at it",
                    dispute.Id, payment.Id, payment.Status);
                throw ServiceDisputeErrors.PaymentNotDecidable();
        }

        if (dispute.NoShow)
            await CompleteNoShowRequestAsync(payment, now, cancellationToken);
    }

    /// <summary>
    /// A no-show decided for the supplier: the work was done, only not marked. The request is completed (a taken job becomes <c>Completato</c> with the
    /// amount the payer paid), so the release, which pays a completed request only, can go on and mark it paid. The caller saves.
    /// </summary>
    private async Task CompleteNoShowRequestAsync(ServiceRequestPayment payment, DateTime now, CancellationToken cancellationToken)
    {
        // The request has two parties and is not tenant-filtered; scoped by the id the payment points to.
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.Id == payment.ServiceRequestId)
            .FirstOrDefaultAsync(cancellationToken);
        if (request is not { Status: ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso })
            return;

        request.Status = ServiceRequestStatus.Completato;
        request.CompletedAt = now;
        request.FinalAmountCents ??= payment.AmountCents;
        if (ServiceRequestJson.ReadPriceLines(request.PriceLinesJson).Count == 0)
            request.PriceLinesJson = payment.LineItemsJson;

        request.UpdatedAt = now;
    }

    // ─── The supplier replies ────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceDisputeSupplierView> ReplyToDisputeAsync(
        Guid serviceRequestId,
        Guid supplierOrgId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var reply = ServiceDisputeRules.NormalizeText(text, required: true, ServiceDisputeLimits.TextField)!;

        var latest = await FindLatestHeldAsync(serviceRequestId, cancellationToken);
        if (latest is not { } known)
            throw ServiceDisputeErrors.DisputeNotFound();

        db.ChangeTracker.Clear();
        ServicePaymentDispute dispute;
        var reachedAdmins = false;
        await using (var transaction = await LockAsync(serviceRequestId, cancellationToken))
        {
            var payment = await LoadPaymentAsync(known.Id, cancellationToken);
            if (payment is not { FundsModel: ServiceFundsModel.PlatformHeld } || payment.SupplierOrgId != supplierOrgId)
                throw ServiceDisputeErrors.DisputeNotFound();

            dispute = await LatestDisputeOfAsync(payment.Id, cancellationToken) ?? throw ServiceDisputeErrors.DisputeNotFound();

            // The same words again (a double click) are the same reply, even once the dispute is over; other words are a second reply.
            if (dispute.SupplierReply is not null && string.Equals(dispute.SupplierReply, reply, StringComparison.Ordinal))
                return ToSupplierView(dispute);

            if (!ServiceDisputeRules.IsOpen(dispute.Status))
                throw ServiceDisputeErrors.DisputeNotOpen();

            if (!ServiceDisputeRules.CanReply(dispute.Status, alreadyReplied: dispute.SupplierReply is not null))
                throw ServiceDisputeErrors.ReplyAlreadyGiven();

            // A reply after the supplier's time is still taken while the dispute is open: the admins read it.
            var now = Now();
            dispute.SupplierReply = reply;
            dispute.SupplierRepliedAt = now;
            dispute.UpdatedAt = now;
            if (dispute.Status == ServiceDisputeStatus.AwaitingSupplier)
            {
                dispute.Status = ServiceDisputeStatus.AwaitingDecision;
                dispute.DecisionDueAt = ServiceDisputeRules.DecisionDueAt(now, options.Value.DisputeDecisionTarget);
                reachedAdmins = true;
            }

            await SaveAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        logger.LogInformation(
            "Dispute {DisputeId} of request {RequestId}: the supplier replied (the admins are told: {ReachedAdmins})",
            dispute.Id, dispute.ServiceRequestId, reachedAdmins);

        var notices = new List<ServicePaymentNotice>
        {
            new(ServicePaymentNoticeKind.DisputeReplied, dispute.ServiceRequestPaymentId, DisputeId: dispute.Id),
        };
        if (reachedAdmins)
        {
            notices.Add(new ServicePaymentNotice(
                ServicePaymentNoticeKind.DisputeAdminAlert, dispute.ServiceRequestPaymentId, Detail: DisputeAlertCodes.Replied, DisputeId: dispute.Id));
        }

        await CompleteAsync(notices, CancellationToken.None);
        return ToSupplierView(dispute);
    }

    // ─── After a refund: the decision is done ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called with the payment locked and its refunds just recorded (<c>ApplyRefundSnapshotsAsync</c>): when a dispute was decided for the customer
    /// and the payment has now given back everything the decision asked for, the decision is done. What the supplier is still owed (the price minus the
    /// commission minus the refund) is due now and the funds job pays it; if nothing is left, the payment already ended as <c>PartiallyRefunded</c>.
    /// The refund and the release never share the same euros: the payment left <c>Disputed</c> only now.
    /// </summary>
    private async Task SettleDecidedDisputeAsync(ServiceRequestPayment payment, DateTime now, CancellationToken cancellationToken)
    {
        if (payment.FundsModel != ServiceFundsModel.PlatformHeld)
            return;

        var dispute = await db.ServicePaymentDisputes
            .Where(d => d.ServiceRequestPaymentId == payment.Id
                        && d.Status == ServiceDisputeStatus.ResolvedForCustomer
                        && d.SettledAt == null)
            .FirstOrDefaultAsync(cancellationToken);
        if (dispute?.RefundTargetCents is not { } target || !ServiceDisputeRules.RefundDone(payment.RefundedCents, target))
            return;

        dispute.SettledAt = now;
        dispute.UpdatedAt = now;

        if (payment.Status == ServicePaymentStatus.Disputed)
        {
            payment.Status = ServicePaymentStatus.Held;
            payment.ReleaseAt = now;
            payment.UpdatedAt = now;
        }

        logger.LogInformation(
            "Dispute {DisputeId}: the refund it decided is done, payment {PaymentId} is {Status}", dispute.Id, payment.Id, payment.Status);
    }

    // ─── Reading ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceDisputePayerView?> FindDisputeForPayerAsync(Guid serviceRequestId, CancellationToken cancellationToken = default)
    {
        var dispute = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.ServiceRequestId == serviceRequestId)
            .OrderByDescending(d => d.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return dispute is null ? null : ToPayerView(dispute);
    }

    public async Task<ServiceDisputeSupplierView?> FindDisputeForSupplierAsync(
        Guid serviceRequestId,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var dispute = await db.ServicePaymentDisputes
            .AsNoTracking()
            .Where(d => d.ServiceRequestId == serviceRequestId && d.SupplierOrgId == supplierOrgId)
            .OrderByDescending(d => d.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return dispute is null ? null : ToSupplierView(dispute);
    }

    /// <summary>What the payer reads: its own words, the state, the decision. Never the supplier's words nor the admin's note.</summary>
    private static ServiceDisputePayerView ToPayerView(ServicePaymentDispute d) => new(
        d.Id,
        d.Status,
        d.Reason,
        d.OpenedBy,
        d.NoShow,
        d.CustomerText,
        d.OpenedAt,
        d.SupplierReplyDueAt,
        SupplierReplied: d.SupplierRepliedAt is not null,
        d.ResolvedAt,
        RefundCents: d.Status == ServiceDisputeStatus.ResolvedForCustomer ? d.DecidedRefundCents : null,
        Settled: d.Status == ServiceDisputeStatus.ResolvedForCustomer && d.SettledAt is not null);

    /// <summary>What the supplier reads: the payer's words, its own reply, the state, the decision. Never the admin's note.</summary>
    private static ServiceDisputeSupplierView ToSupplierView(ServicePaymentDispute d) => new(
        d.Id,
        d.ServiceRequestId,
        d.Status,
        d.Reason,
        d.OpenedBy,
        d.NoShow,
        d.CustomerText,
        d.OpenedAt,
        d.SupplierReplyDueAt,
        d.SupplierReply,
        d.SupplierRepliedAt,
        CanReply: ServiceDisputeRules.CanReply(d.Status, alreadyReplied: d.SupplierReply is not null),
        d.ResolvedAt,
        RefundCents: d.Status == ServiceDisputeStatus.ResolvedForCustomer ? d.DecidedRefundCents : null);
}

/// <summary>The codes of the alerts to the admins about a dispute (the <c>Detail</c> of a <see cref="ServicePaymentNoticeKind.DisputeAdminAlert"/>): ids and codes only.</summary>
internal static class DisputeAlertCodes
{
    /// <summary>The supplier replied: the dispute is waiting for a decision.</summary>
    public const string Replied = "awaiting_decision:replied";

    /// <summary>The supplier did not reply in time: the dispute is waiting for a decision.</summary>
    public const string NoReply = "awaiting_decision:no_reply";

    /// <summary>The target to decide has passed.</summary>
    public const string Overdue = "decision_overdue";

    /// <summary>The refund that was decided did not go through.</summary>
    public const string RefundIncomplete = "refund_incomplete";
}
