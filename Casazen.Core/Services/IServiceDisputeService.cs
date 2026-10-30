using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

// ─── What goes in ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A payer's complaint about a job paid through CasaZen ("Segnala un problema"), or the no-show CasaZen opens itself.</summary>
/// <param name="ServiceRequestId">The request. The caller proved who it is: the showcase customer by code and e-mail, the host by owning the request.</param>
/// <param name="OpenedBy">The customer of a showcase, the host, or CasaZen (the job).</param>
/// <param name="Reason">What is wrong.</param>
/// <param name="Text">What the payer wrote: at most <see cref="ServiceDisputeLimits.TextMaxLength"/> characters, required for <see cref="ServiceDisputeReason.Other"/>.</param>
/// <param name="ActorOrgId">The host org that complains (<see cref="ServiceDisputeOpener.Host"/>): it must be the payer of the payment.</param>
/// <param name="ActorUserId">The host member who complains; kept with the dispute.</param>
public sealed record OpenServiceDisputeRequest(
    Guid ServiceRequestId,
    ServiceDisputeOpener OpenedBy,
    ServiceDisputeReason Reason,
    string? Text,
    Guid? ActorOrgId = null,
    string? ActorUserId = null);

/// <summary>The decision of an admin about a dispute.</summary>
/// <param name="Outcome"><c>for_supplier</c> (the complaint is unfounded: the supplier is paid) or <c>for_customer</c> (the supplier was at fault).</param>
/// <param name="RefundCents">For the customer: what to give back, in cents, 1 to the supplier's part that is left; empty means all of it. Not allowed for the supplier.</param>
/// <param name="Note">The admin's internal note; never shown to the parties.</param>
public sealed record ResolveServiceDisputeRequest(string? Outcome, int? RefundCents, string? Note);

/// <summary>The filters of the admin's list of disputes.</summary>
/// <param name="Status">One status, or null for all.</param>
/// <param name="SupplierOrgId">One supplier, or null for all.</param>
/// <param name="OpenOnly">Only the disputes that are not decided yet.</param>
public sealed record AdminServiceDisputeQuery(ServiceDisputeStatus? Status, Guid? SupplierOrgId, bool OpenOnly, int Page, int PageSize);

// ─── What comes out: one view per audience, so a view cannot carry what its audience must not read ───────────────────

/// <summary>What the payer (the showcase customer, the host) reads of its complaint: its own words, the state, the decision. Never the supplier's words nor the admin's note.</summary>
/// <param name="Id">The dispute.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Reason">What the payer said was wrong.</param>
/// <param name="OpenedBy">Who opened it (CasaZen itself for a no-show).</param>
/// <param name="NoShow">The supplier never marked the work completed.</param>
/// <param name="Text">The payer's own words.</param>
/// <param name="OpenedAt">When it was opened.</param>
/// <param name="SupplierReplyDueAt">When the supplier's time to reply ends.</param>
/// <param name="SupplierReplied">The supplier replied (what it wrote is not shown to the payer).</param>
/// <param name="ResolvedAt">When it was decided or withdrawn.</param>
/// <param name="RefundCents">What the payer gets back, when decided for the customer.</param>
/// <param name="Settled">The refund that was decided has been made.</param>
public sealed record ServiceDisputePayerView(
    Guid Id,
    ServiceDisputeStatus Status,
    ServiceDisputeReason Reason,
    ServiceDisputeOpener OpenedBy,
    bool NoShow,
    string? Text,
    DateTime OpenedAt,
    DateTime SupplierReplyDueAt,
    bool SupplierReplied,
    DateTime? ResolvedAt,
    int? RefundCents,
    bool Settled);

/// <summary>What the supplier reads of a complaint against it: the payer's words, its own reply, the state and the decision. Never the admin's note.</summary>
/// <param name="Id">The dispute.</param>
/// <param name="ServiceRequestId">The request.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Reason">What the payer said was wrong.</param>
/// <param name="OpenedBy">Who opened it.</param>
/// <param name="NoShow">The supplier never marked the work completed.</param>
/// <param name="CustomerText">The payer's words.</param>
/// <param name="OpenedAt">When it was opened.</param>
/// <param name="ReplyDueAt">When the supplier's time to reply ends.</param>
/// <param name="Reply">The supplier's reply.</param>
/// <param name="RepliedAt">When it replied.</param>
/// <param name="CanReply">It can still reply (the dispute is open and it has not replied).</param>
/// <param name="ResolvedAt">When it was decided or withdrawn.</param>
/// <param name="RefundCents">What the payer got back, when decided for the customer (the supplier loses it).</param>
public sealed record ServiceDisputeSupplierView(
    Guid Id,
    Guid ServiceRequestId,
    ServiceDisputeStatus Status,
    ServiceDisputeReason Reason,
    ServiceDisputeOpener OpenedBy,
    bool NoShow,
    string? CustomerText,
    DateTime OpenedAt,
    DateTime ReplyDueAt,
    string? Reply,
    DateTime? RepliedAt,
    bool CanReply,
    DateTime? ResolvedAt,
    int? RefundCents);

/// <summary>One line of the admin's list of disputes: the facts the work queue needs, no words of the parties.</summary>
public sealed record AdminServiceDisputeSummary(
    Guid Id,
    Guid ServiceRequestId,
    Guid ServiceRequestPaymentId,
    Guid SupplierOrgId,
    string SupplierName,
    ServiceDisputeStatus Status,
    ServiceDisputeReason Reason,
    ServiceDisputeOpener OpenedBy,
    bool NoShow,
    DateTime OpenedAt,
    DateTime SupplierReplyDueAt,
    DateTime? SupplierRepliedAt,
    DateTime? DecisionDueAt,
    bool Overdue,
    DateTime? ResolvedAt,
    int AmountCents,
    int NetCents,
    int RefundedCents,
    int RefundableCents,
    int? DecidedRefundCents,
    bool Settled);

/// <summary>One dispute for the admin who decides it: everything, the words of both parties and its own note included.</summary>
/// <param name="Summary">The line of the list.</param>
/// <param name="PaymentStatus">The status of the payment (<see cref="ServicePaymentStatus.Disputed"/> while the money is stopped).</param>
/// <param name="CommissionCents">The commission CasaZen keeps in any case.</param>
/// <param name="ServiceName">The service, as the request names it.</param>
/// <param name="RequestStatus">Where the request is.</param>
/// <param name="ScheduledEndUtc">When the time agreed for the work ends.</param>
/// <param name="CustomerText">The payer's words.</param>
/// <param name="SupplierReply">The supplier's reply.</param>
/// <param name="AdminNote">The note of the admin who decided.</param>
/// <param name="ResolvedByUserId">The admin who decided.</param>
public sealed record AdminServiceDisputeDetail(
    AdminServiceDisputeSummary Summary,
    ServicePaymentStatus PaymentStatus,
    int CommissionCents,
    string ServiceName,
    ServiceRequestStatus RequestStatus,
    DateTime? ScheduledEndUtc,
    string? CustomerText,
    string? SupplierReply,
    string? AdminNote,
    string? ResolvedByUserId);

/// <summary>What one run of the dispute step of the funds job did.</summary>
/// <param name="ReachedAdmins">Disputes whose supplier did not reply in time: they went to the admins.</param>
/// <param name="Overdue">Disputes whose decision target passed: the admins were told.</param>
/// <param name="RefundAlerts">Decisions whose refund did not go through: the admins were told.</param>
/// <param name="NoShowsOpened">Complaints CasaZen opened for a job nobody marked completed.</param>
/// <param name="Errors">Disputes the run could not handle (logged; the next run retries them).</param>
public sealed record ServiceDisputesRun(int ReachedAdmins, int Overdue, int RefundAlerts, int NoShowsOpened, int Errors)
{
    /// <summary>A run that found nothing to do.</summary>
    public static ServiceDisputesRun Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>The run did something or failed on something worth a log line.</summary>
    public bool HadWork => ReachedAdmins + Overdue + RefundAlerts + NoShowsOpened + Errors > 0;
}

// ─── The service ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The complaint and the dispute about a job paid through CasaZen (SP-15c-c, decisions VT-D1 and VT-D16): the payer reports a problem within the
/// window and the money CasaZen holds stops; the supplier replies; an admin decides — for the supplier (it is paid) or for the customer (the payer
/// gets back what the admin decides, at most the price minus the commission; CasaZen keeps its commission and the supplier is penalized).
/// </summary>
/// <remarks>
/// <para><b>Everything is under the payment lock</b> of the request (the same advisory lock the release and the refunds take, key: the request id) and
/// reads the payment again after it, so a complaint and a release never share the same euros: the one that takes the lock first wins and the other is
/// refused or finds the dispute. Every method that changes a dispute is idempotent: asking twice does what asking once does, and a decision that races
/// another is a 409 (<see cref="ServiceDisputeErrors.StateChanged"/>), never two decisions.</para>
/// <para><b>Stripe is called only for the refund of a decision for the customer</b>, outside the lock, with the idempotency key of the refund row that
/// was reserved with the decision. The payment stays <see cref="ServicePaymentStatus.Disputed"/> until that refund has succeeded; if it cannot, the
/// admins are told and the payment is not paid out.</para>
/// <para>The creation of a complaint is not behind the feature flag <c>SupplierOnlinePayments</c> (the money it stops is already held); the webhook
/// events and the job that follow it are not either.</para>
/// </remarks>
public interface IServiceDisputeService
{
    // ─── The payer ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the complaint and stops the money (<see cref="ServicePaymentStatus.Held"/> → <see cref="ServicePaymentStatus.Disputed"/>). A complaint
    /// already open on the payment answers the same dispute and writes nothing. 422 <see cref="ServiceDisputeErrors.NotReportable"/> (no money held by
    /// CasaZen, or a request that was not done), <see cref="ServiceDisputeErrors.WindowClosed"/> (the money is due, released, or the payer said "all fine"),
    /// <see cref="ServiceDisputeErrors.TooEarly"/> (the work is not completed and its time has not ended), <see cref="ServiceDisputeErrors.Invalid"/>
    /// (<c>fields</c>); 404 when the request is not the caller's payment.
    /// </summary>
    Task<ServiceDisputePayerView> OpenDisputeAsync(OpenServiceDisputeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payer takes its complaint back: the money is no longer stopped and the supplier is paid at the next run of the funds job. Withdrawing a
    /// complaint that was withdrawn already answers the same; 409 <see cref="ServiceDisputeErrors.NotOpen"/> for one that was decided.
    /// </summary>
    Task<ServiceDisputePayerView> WithdrawDisputeAsync(
        Guid serviceRequestId,
        ServiceDisputeOpener party,
        Guid? hostOrgId,
        CancellationToken cancellationToken = default);

    /// <summary>The latest dispute of the request as the payer reads it; null when there is none.</summary>
    Task<ServiceDisputePayerView?> FindDisputeForPayerAsync(Guid serviceRequestId, CancellationToken cancellationToken = default);

    // ─── The supplier ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The latest dispute of the supplier's request as the supplier reads it; null when there is none (or the request is another supplier's).</summary>
    Task<ServiceDisputeSupplierView?> FindDisputeForSupplierAsync(Guid serviceRequestId, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier's one reply: the dispute goes to the admins (<see cref="ServiceDisputeStatus.AwaitingDecision"/>). A reply after the supplier's
    /// time is taken while the dispute is open. 404 for a request that is not the supplier's or has no dispute; 409
    /// <see cref="ServiceDisputeErrors.NotOpen"/>, <see cref="ServiceDisputeErrors.AlreadyReplied"/>; 422 <see cref="ServiceDisputeErrors.Invalid"/>.
    /// </summary>
    Task<ServiceDisputeSupplierView> ReplyToDisputeAsync(
        Guid serviceRequestId,
        Guid supplierOrgId,
        string? text,
        CancellationToken cancellationToken = default);

    // ─── The admins ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The disputes, paginated in SQL: with <c>OpenOnly</c> the work queue, the oldest first; otherwise the newest first.</summary>
    Task<(IReadOnlyList<AdminServiceDisputeSummary> Items, int TotalCount)> ListDisputesAsync(
        AdminServiceDisputeQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>One dispute with the words of both parties and what an admin needs to decide. 404 <see cref="ServiceDisputeErrors.NotFound"/>.</summary>
    Task<AdminServiceDisputeDetail> GetDisputeAsync(Guid disputeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides a dispute that reached the admins. For the supplier: the payment goes back to <see cref="ServicePaymentStatus.Held"/>, due now, and
    /// the supplier is paid by the funds job. For the customer: the refund of the amount decided is reserved in the same transaction (the supplier is not
    /// paid in between), asked of Stripe after it, and the payment leaves <see cref="ServicePaymentStatus.Disputed"/> only when it has succeeded; the
    /// supplier is penalized. The same decision twice answers the same; another decision on a decided dispute is a 409. Audited.
    /// </summary>
    Task<AdminServiceDisputeDetail> ResolveDisputeAsync(
        Guid disputeId,
        ResolveServiceDisputeRequest request,
        string actorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The system job of the disputes (SP-15c-c), run by the funds job (<c>service-funds</c>, every five minutes): it sends to the admins the disputes whose
/// supplier did not reply in time, tells them when their target passed or the refund of a decision did not go through, and opens the complaint for a
/// job nobody marked completed once the time agreed for it ended (the no-show). Each dispute is handled on its own, under its lock; an error on one
/// is logged and counted, and the others go on.
/// </summary>
public interface IServiceDisputeJobService
{
    /// <summary>Runs the four steps. Always scheduled: it follows money that is already stopped.</summary>
    Task<ServiceDisputesRun> RunDisputesAsync(CancellationToken cancellationToken = default);
}

// ─── The penalty in the ranking ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>What a supplier is penalized for.</summary>
public enum SupplierDisputePenaltyKind
{
    /// <summary>A dispute was decided for the customer: the supplier was at fault.</summary>
    DisputeLost,

    /// <summary>The supplier never marked the work completed and the dispute about it was decided for the customer.</summary>
    NoShow,
}

/// <summary>An event that counts against a supplier in the ranking.</summary>
/// <param name="SupplierOrgId">The supplier.</param>
/// <param name="DisputeId">The dispute that decided it (the source of truth: the event can be rebuilt from it).</param>
/// <param name="ServiceRequestId">The request.</param>
/// <param name="Kind">What it is.</param>
/// <param name="At">When it was decided.</param>
public sealed record SupplierDisputePenalty(Guid SupplierOrgId, Guid DisputeId, Guid ServiceRequestId, SupplierDisputePenaltyKind Kind, DateTime At);

/// <summary>
/// Where the penalty of a lost dispute goes (SP-15c-c, VT-D16/VT-D17). The weights of the ranking and the window of the events belong to the
/// reliability of the supplier (<c>SP-REL</c>, VT-D17): until they are approved nothing shows publicly. The default implementation only logs; a
/// failure of a sink never undoes a decision (it is logged), and the decision itself — the dispute row — is the source from which the events can be
/// rebuilt.
/// </summary>
public interface ISupplierDisputePenalties
{
    /// <summary>Records the penalty. Idempotent per <see cref="SupplierDisputePenalty.DisputeId"/>.</summary>
    Task RecordAsync(SupplierDisputePenalty penalty, CancellationToken cancellationToken = default);
}
