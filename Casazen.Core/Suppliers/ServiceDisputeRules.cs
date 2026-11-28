using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

public static class ServiceDisputeLimits
{
    public const int TextMaxLength = 1000;

    public const string ReasonField = "reason";

    public const string TextField = "text";

    public const string NoteField = "note";

    public const string RefundField = "refundCents";
}

public enum DisputeReportVerdict
{
    Allowed,

    AllowedNoShow,

    AlreadyOpen,

    WindowClosed,

    TooEarly,

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

public enum DisputeRefundKind
{
    Refund,

    NothingLeft,

    Invalid,

    Exceeds,
}

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
    public static IReadOnlyList<string> ReasonNames { get; } = Enum.GetValues<ServiceDisputeReason>().Select(ReasonName).ToArray();

    public static string ReasonName(ServiceDisputeReason reason) => reason switch
    {
        ServiceDisputeReason.NotDone => "not_done",
        ServiceDisputeReason.Incomplete => "incomplete",
        ServiceDisputeReason.PoorQuality => "poor_quality",
        ServiceDisputeReason.NotAsDescribed => "not_as_described",
        _ => "other",
    };

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

    public static string OpenerName(ServiceDisputeOpener opener) => opener switch
    {
        ServiceDisputeOpener.Customer => "customer",
        ServiceDisputeOpener.Host => "host",
        _ => "system",
    };

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

    public static bool IsOpen(ServiceDisputeStatus status) =>
        status is ServiceDisputeStatus.AwaitingSupplier or ServiceDisputeStatus.AwaitingDecision;

    public static bool IsResolved(ServiceDisputeStatus status) => !IsOpen(status);

    public static bool CanReply(ServiceDisputeStatus status, bool alreadyReplied) => IsOpen(status) && !alreadyReplied;

    public static bool CanWithdraw(ServiceDisputeStatus status) => IsOpen(status);

    public static bool CanResolve(ServiceDisputeStatus status) => status == ServiceDisputeStatus.AwaitingDecision;

    public static DateTime SupplierReplyDueAt(DateTime openedAt, TimeSpan replyTime) => openedAt + replyTime;

    public static DateTime DecisionDueAt(DateTime reachedAt, TimeSpan target) => reachedAt + target;

    public static DateTime NoShowOpensAt(DateTime scheduledEndUtc, TimeSpan noShowTime) => scheduledEndUtc + noShowTime;

    public static bool ReplyTermPassed(ServiceDisputeStatus status, DateTime supplierReplyDueAt, DateTime now) =>
        status == ServiceDisputeStatus.AwaitingSupplier && now >= supplierReplyDueAt;

    public static bool DecisionOverdue(ServiceDisputeStatus status, DateTime? decisionDueAt, DateTime? alertedAt, DateTime now) =>
        status == ServiceDisputeStatus.AwaitingDecision && alertedAt is null && decisionDueAt is { } due && now >= due;

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

        if (facts.PaymentStatus is ServicePaymentStatus.Released or ServicePaymentStatus.TransferFailed)
            return DisputeReportVerdict.WindowClosed;

        if (facts.PaymentStatus != ServicePaymentStatus.Held)
            return DisputeReportVerdict.NotReportable;

        switch (facts.RequestStatus)
        {
            case ServiceRequestStatus.Completato:
                if (facts.ReleaseConfirmedAt is not null)
                    return DisputeReportVerdict.WindowClosed;

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

    public static int RefundTarget(int refundedCents, int refundsInProgressCents, int decidedCents) =>
        (int)Math.Min((long)refundedCents + refundsInProgressCents + decidedCents, int.MaxValue);

    public static bool RefundDone(int paymentRefundedCents, int refundTargetCents) => paymentRefundedCents >= refundTargetCents;

    public static SupplierDisputePenaltyKind? PenaltyFor(ServiceDisputeStatus outcome, bool noShow) => outcome switch
    {
        ServiceDisputeStatus.ResolvedForCustomer => noShow ? SupplierDisputePenaltyKind.NoShow : SupplierDisputePenaltyKind.DisputeLost,
        _ => null,
    };

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
