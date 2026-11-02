using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

/// <summary>Sizes of the words of a dispute (SP-15c-c).</summary>
public static class ServiceDisputeLimits
{
    /// <summary>Longest text of a complaint, of a reply and of an admin's note, in characters. Plain text: no photos in v1 (VT-D12).</summary>
    public const int TextMaxLength = 1000;

    /// <summary>The field names the API reports in <c>fields</c> for a text at fault.</summary>
    public const string ReasonField = "reason";

    /// <inheritdoc cref="ReasonField"/>
    public const string TextField = "text";

    /// <inheritdoc cref="ReasonField"/>
    public const string NoteField = "note";

    /// <inheritdoc cref="ReasonField"/>
    public const string RefundField = "refundCents";
}

/// <summary>What <see cref="ServiceDisputeRules.CanReport"/> decides about a payer's complaint.</summary>
public enum DisputeReportVerdict
{
    /// <summary>The work is completed and the time to report a problem has not ended: the money is stopped.</summary>
    Allowed,

    /// <summary>The supplier never marked the work completed and the time agreed for it has ended (the no-show): the money is stopped.</summary>
    AllowedNoShow,

    /// <summary>A complaint is already open on this payment: it is the same one (nothing new is written).</summary>
    AlreadyOpen,

    /// <summary>The time is over: the payer said "all fine", the money is due, or it was paid to the supplier.</summary>
    WindowClosed,

    /// <summary>The work is not completed and the time agreed for it has not ended (or no time was agreed): not yet.</summary>
    TooEarly,

    /// <summary>There is nothing to complain about from here: no money held by CasaZen for this job, or a request that was not done and is not due.</summary>
    NotReportable,
}

/// <summary>What <see cref="ServiceDisputeRules.CanReport"/> looks at: a plain value, so every combination is a test.</summary>
/// <param name="PaymentStatus">The status of the payment held by CasaZen.</param>
/// <param name="ReleaseAt">When the money is due to the supplier; null until the work is completed.</param>
/// <param name="ReleaseConfirmedAt">When the payer said "all fine".</param>
/// <param name="RequestStatus">Where the request is.</param>
/// <param name="ScheduledEndUtc">When the time agreed for the work ends; null when none was agreed.</param>
/// <param name="HasOpenDispute">A dispute of this payment is open.</param>
public sealed record DisputeReportFacts(
    ServicePaymentStatus PaymentStatus,
    DateTime? ReleaseAt,
    DateTime? ReleaseConfirmedAt,
    ServiceRequestStatus RequestStatus,
    DateTime? ScheduledEndUtc,
    bool HasOpenDispute);

/// <summary>The decision of the amount an admin gives back to the payer (<see cref="ServiceDisputeRules.DecideRefund"/>).</summary>
public enum DisputeRefundKind
{
    /// <summary>Give back <see cref="DisputeRefundDecision.AmountCents"/>.</summary>
    Refund,

    /// <summary>Nothing is left of the supplier's part to give back (it was all refunded already): the decision stands with no money.</summary>
    NothingLeft,

    /// <summary>The amount asked for is not a positive whole number of cents.</summary>
    Invalid,

    /// <summary>The amount asked for is more than what is left of the supplier's part.</summary>
    Exceeds,
}

/// <summary>An amount to give back to the payer, or why it cannot be.</summary>
/// <param name="Kind">What the amount asked for turned out to be.</param>
/// <param name="AmountCents">The amount to give back (<see cref="DisputeRefundKind.Refund"/>), or the most that could be (<see cref="DisputeRefundKind.Exceeds"/>).</param>
public readonly record struct DisputeRefundDecision(DisputeRefundKind Kind, int AmountCents);

/// <summary>
/// The rules of the complaint and the dispute about a job paid through CasaZen (SP-15c-c, decision VT-D16). <b>Pure functions</b>: no clock, no
/// configuration, no database, no Stripe. Every amount is whole euro cents.
/// </summary>
/// <remarks>
/// <para><b>The money of a decision.</b> For the customer the payer gets back at most what the supplier would have been paid — the price minus the
/// commission minus what was refunded already: <see cref="HeldPaymentRules.ReleasableCents"/> — and CasaZen keeps its commission (the supplier's loss
/// and CasaZen's gain, as the product owner decided). For the supplier, or when the payer withdraws, nothing is given back and the supplier is paid as
/// if nothing had happened. The refund and the release never share the same euros: the payment stays <see cref="ServicePaymentStatus.Disputed"/> —
/// which <see cref="HeldPaymentRules.Release"/> refuses — until the refund that was decided has succeeded.</para>
/// <para><b>The time.</b> A complaint is possible from the completion of the work until the money is due (<c>ReleaseAt</c>: the 60 minutes after the
/// completion, or the charge) and never after the payer said "all fine" or the money was released; for a job nobody marked completed it is possible
/// from the end of the time agreed for it, and CasaZen opens it itself after <c>DisputeNoShowHours</c>.</para>
/// </remarks>
public static class ServiceDisputeRules
{
    // ─── Names in the API ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The names of the reasons in the API (<c>reason</c>).</summary>
    public static IReadOnlyList<string> ReasonNames { get; } = Enum.GetValues<ServiceDisputeReason>().Select(ReasonName).ToArray();

    public static string ReasonName(ServiceDisputeReason reason) => reason switch
    {
        ServiceDisputeReason.NotDone => "not_done",
        ServiceDisputeReason.Incomplete => "incomplete",
        ServiceDisputeReason.PoorQuality => "poor_quality",
        ServiceDisputeReason.NotAsDescribed => "not_as_described",
        _ => "other",
    };

    /// <summary>Reads the name of a reason as the API spells it (exactly, lowercase); anything else is not a reason.</summary>
    public static bool TryParseReason(string? value, out ServiceDisputeReason reason)
    {
        foreach (var candidate in Enum.GetValues<ServiceDisputeReason>())
        {
            if (string.Equals(ReasonName(candidate), value?.Trim(), StringComparison.Ordinal))
            {
                reason = candidate;
                return true;
            }
        }

        reason = default;
        return false;
    }

    /// <summary>Who opened the dispute, as the API names it: <c>customer</c>, <c>host</c> or <c>system</c>. The statuses are named as the enum names (<c>AwaitingSupplier</c>, <c>AwaitingDecision</c>, <c>ResolvedForSupplier</c>, <c>ResolvedForCustomer</c>, <c>Withdrawn</c>), like the statuses of the payments.</summary>
    public static string OpenerName(ServiceDisputeOpener opener) => opener switch
    {
        ServiceDisputeOpener.Customer => "customer",
        ServiceDisputeOpener.Host => "host",
        _ => "system",
    };

    /// <summary>The outcome an admin picks (<c>for_supplier</c> or <c>for_customer</c>); anything else is not one.</summary>
    public static bool TryParseOutcome(string? value, out ServiceDisputeStatus outcome)
    {
        switch (value?.Trim())
        {
            case "for_supplier":
                outcome = ServiceDisputeStatus.ResolvedForSupplier;
                return true;
            case "for_customer":
                outcome = ServiceDisputeStatus.ResolvedForCustomer;
                return true;
            default:
                outcome = default;
                return false;
        }
    }

    // ─── Which state allows which action ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The dispute is not decided or withdrawn: the money it concerns is stopped.</summary>
    public static bool IsOpen(ServiceDisputeStatus status) =>
        status is ServiceDisputeStatus.AwaitingSupplier or ServiceDisputeStatus.AwaitingDecision;

    /// <summary>The dispute ended, one way or another.</summary>
    public static bool IsResolved(ServiceDisputeStatus status) => !IsOpen(status);

    /// <summary>The supplier may reply while the dispute is open and it has not replied (a reply after its time is still taken).</summary>
    public static bool CanReply(ServiceDisputeStatus status, bool alreadyReplied) => IsOpen(status) && !alreadyReplied;

    /// <summary>The payer may take its complaint back while the dispute is open.</summary>
    public static bool CanWithdraw(ServiceDisputeStatus status) => IsOpen(status);

    /// <summary>An admin decides a dispute that reached it; not one whose supplier still has time to reply.</summary>
    public static bool CanResolve(ServiceDisputeStatus status) => status == ServiceDisputeStatus.AwaitingDecision;

    // ─── The time ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The instant the supplier's time to reply ends.</summary>
    public static DateTime SupplierReplyDueAt(DateTime openedAt, TimeSpan replyTime) => openedAt + replyTime;

    /// <summary>The instant the admins aim to have decided by, counted from the moment the dispute reached them.</summary>
    public static DateTime DecisionDueAt(DateTime reachedAt, TimeSpan target) => reachedAt + target;

    /// <summary>The instant CasaZen opens the no-show complaint itself: <paramref name="noShowTime"/> after the end of the time agreed for the work.</summary>
    public static DateTime NoShowOpensAt(DateTime scheduledEndUtc, TimeSpan noShowTime) => scheduledEndUtc + noShowTime;

    /// <summary>The supplier's time to reply has passed without a reply: the dispute goes to the admins.</summary>
    public static bool ReplyTermPassed(ServiceDisputeStatus status, DateTime supplierReplyDueAt, DateTime now) =>
        status == ServiceDisputeStatus.AwaitingSupplier && now >= supplierReplyDueAt;

    /// <summary>The admins' target has passed and they were not told yet.</summary>
    public static bool DecisionOverdue(ServiceDisputeStatus status, DateTime? decisionDueAt, DateTime? alertedAt, DateTime now) =>
        status == ServiceDisputeStatus.AwaitingDecision && alertedAt is null && decisionDueAt is { } due && now >= due;

    /// <summary>
    /// The refund of a decision for the customer has not gone through: it was decided more than <paramref name="grace"/> ago, the payment has not
    /// given back what was decided, no refund is waiting for Stripe, and the admins were not told yet.
    /// </summary>
    public static bool RefundAlertDue(
        ServiceDisputeStatus status,
        DateTime? resolvedAt,
        DateTime? settledAt,
        DateTime? alertedAt,
        int refundedCents,
        int refundTargetCents,
        int refundsInProgressCents,
        TimeSpan grace,
        DateTime now) =>
        status == ServiceDisputeStatus.ResolvedForCustomer
        && settledAt is null
        && alertedAt is null
        && refundsInProgressCents == 0
        && refundedCents < refundTargetCents
        && resolvedAt is { } at
        && now >= at + grace;

    // ─── Whether a payer may complain ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether the payer of a job may report a problem now. The order of the checks is the safety of the money: a dispute already open is the
    /// same one; only money CasaZen holds and has not released can be stopped; a completed job can be disputed until the money is due and never after
    /// the payer's "all fine"; a job nobody marked completed only once the time agreed for it has ended.
    /// </summary>
    public static DisputeReportVerdict CanReport(DisputeReportFacts facts, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.HasOpenDispute)
            return DisputeReportVerdict.AlreadyOpen;

        // The money was released (or is being, or the supplier was paid some other way): the time is over.
        if (facts.PaymentStatus is ServicePaymentStatus.Released or ServicePaymentStatus.TransferFailed)
            return DisputeReportVerdict.WindowClosed;

        // Only money CasaZen holds can be stopped; a card block, a payment under review, a refunded one or a decided dispute cannot.
        if (facts.PaymentStatus != ServicePaymentStatus.Held)
            return DisputeReportVerdict.NotReportable;

        switch (facts.RequestStatus)
        {
            case ServiceRequestStatus.Completato:
                if (facts.ReleaseConfirmedAt is not null)
                    return DisputeReportVerdict.WindowClosed;

                // A completed job always has the moment its money is due; one that has none yet (it cannot be released) can be disputed.
                return facts.ReleaseAt is { } due && now >= due ? DisputeReportVerdict.WindowClosed : DisputeReportVerdict.Allowed;

            case ServiceRequestStatus.Pagato:
                return DisputeReportVerdict.WindowClosed;

            case ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso:
                return facts.ScheduledEndUtc is { } end && now >= end
                    ? DisputeReportVerdict.AllowedNoShow
                    : DisputeReportVerdict.TooEarly;

            default:
                return DisputeReportVerdict.NotReportable;
        }
    }

    /// <summary>
    /// When the time to report a problem ends, for the page that tells the payer: the moment the money is due, while the job is completed and the
    /// payer has not said "all fine". Null otherwise (no window, or one that is not a point in time).
    /// </summary>
    public static DateTime? ReportableUntil(DisputeReportFacts facts) =>
        facts is { PaymentStatus: ServicePaymentStatus.Held, RequestStatus: ServiceRequestStatus.Completato, ReleaseConfirmedAt: null, HasOpenDispute: false }
            ? facts.ReleaseAt
            : null;

    // ─── The money of a decision ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What an admin may give back to the payer of a payment: the supplier's part that is left — the price minus the commission, minus what was
    /// refunded and what is being refunded. Never negative; the commission is never given back by a dispute.
    /// </summary>
    public static int RefundableToPayer(int netCents, int refundedCents, int refundsInProgressCents) =>
        HeldPaymentRules.ReleasableCents(netCents, refundedCents, refundsInProgressCents);

    /// <summary>
    /// Decides the amount to give back: nothing asked for means everything that can be; an amount must be a positive whole number of cents and not
    /// more than <paramref name="refundableCents"/>. When nothing is left to give back the decision stands without money (the supplier is penalized
    /// all the same) and an amount is refused.
    /// </summary>
    public static DisputeRefundDecision DecideRefund(int? requestedCents, int refundableCents)
    {
        if (refundableCents <= 0)
            return requestedCents is null ? new(DisputeRefundKind.NothingLeft, 0) : new(DisputeRefundKind.Exceeds, 0);

        if (requestedCents is null)
            return new(DisputeRefundKind.Refund, refundableCents);

        if (requestedCents <= 0)
            return new(DisputeRefundKind.Invalid, 0);

        return requestedCents > refundableCents
            ? new(DisputeRefundKind.Exceeds, refundableCents)
            : new(DisputeRefundKind.Refund, requestedCents.Value);
    }

    /// <summary>The total a payment must have given back for the decision to be done: what is refunded or being refunded, plus what was decided.</summary>
    public static int RefundTarget(int refundedCents, int refundsInProgressCents, int decidedCents) =>
        (int)Math.Min((long)refundedCents + refundsInProgressCents + decidedCents, int.MaxValue);

    /// <summary>The refund of a decision is done: the payment has given back everything the decision asked for.</summary>
    public static bool RefundDone(int paymentRefundedCents, int refundTargetCents) => paymentRefundedCents >= refundTargetCents;

    // ─── The penalty ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a decision costs the supplier in the ranking: a dispute lost, or a no-show when the supplier never marked the work completed. Nothing for a
    /// decision for the supplier or a complaint taken back.
    /// </summary>
    public static SupplierDisputePenaltyKind? PenaltyFor(ServiceDisputeStatus outcome, bool noShow) => outcome switch
    {
        ServiceDisputeStatus.ResolvedForCustomer => noShow ? SupplierDisputePenaltyKind.NoShow : SupplierDisputePenaltyKind.DisputeLost,
        _ => null,
    };

    // ─── The words ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The text of a complaint, of a reply or of an admin's note: lines normalized, trimmed, at most <see cref="ServiceDisputeLimits.TextMaxLength"/>
    /// characters and no control characters. <c>null</c> when there is none and none is <paramref name="required"/>.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">422 <see cref="ServiceDisputeErrors.Invalid"/> for <paramref name="field"/>.</exception>
    public static string? NormalizeText(string? text, bool required, string field)
    {
        var value = text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (string.IsNullOrEmpty(value))
            return required ? throw ServiceDisputeErrors.InvalidFields(field) : null;

        if (value.Length > ServiceDisputeLimits.TextMaxLength || value.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
            throw ServiceDisputeErrors.InvalidFields(field);

        return value;
    }
}
