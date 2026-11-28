using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

public enum GuestCancellationChannel
{
    GuestArea,

    SignedLink,
}

public static class GuestCancellationKinds
{
    public const string Cancel = "cancel";

    public const string WithdrawRequest = "withdrawRequest";

    public const string DeferredNoCharge = "deferredNoCharge";
}

public static class GuestCancellationReasons
{
    public const string AlreadyCancelled = "already_cancelled";
    public const string StayStarted = "stay_started";
    public const string NotCancellableStatus = "not_cancellable_status";
    public const string PaymentInProgress = "payment_in_progress";
}

public static class GuestCancellationErrorCodes
{
    public const string RefundChanged = "refund_changed";

    public const string StayStarted = "guest_cancel_stay_started";

    public const string ConfirmationRequired = "guest_cancel_confirmation_required";
}

public sealed record GuestCancellationPolicyInfo(string Slug, string Name, string? Description);

public sealed record GuestCancellationStay(string PropertyName, DateOnly CheckInDate, DateOnly CheckOutDate);

public sealed record GuestCancellationOutcome(decimal RefundAmount, string RefundStatus);

public static class GuestCancellationRefundStatuses
{
    public const string None = "none";

    public const string Pending = "pending";

    public const string Confirmed = "confirmed";

    /// <summary>Stripe refused a refund; the host sees it and retries.</summary>
    public const string Failed = "failed";

    public static string Of(IReadOnlyCollection<Entities.PaymentRefund> refunds)
    {
        ArgumentNullException.ThrowIfNull(refunds);
        if (refunds.Count == 0)
            return None;

        if (refunds.Any(r => r.Status is Entities.PaymentRefundStatus.Pending or Entities.PaymentRefundStatus.RequiresAction))
            return Pending;

        return refunds.Any(r => r.Status is Entities.PaymentRefundStatus.Failed or Entities.PaymentRefundStatus.Canceled)
            ? Failed
            : Confirmed;
    }
}

/// <summary>
/// What cancelling a booking would do now, as the guest sees it before confirming (DB-08a): one figure, the one the cancellation
/// itself refunds (<see cref="CancellationRefundPolicy.EvaluateGuestRefund(Entities.Booking, DateTimeOffset, decimal)"/>), so the
/// amount shown is the amount refunded. Nothing personal and no booking code.
/// </summary>
/// <param name="Cancellable">The guest can cancel now from where they are.</param>
/// <param name="Reason">Why not, when <paramref name="Cancellable"/> is false (<see cref="GuestCancellationReasons"/>).</param>
/// <param name="Kind"><see cref="GuestCancellationKinds"/>.</param>
/// <param name="Currency">ISO 4217 code of the property (<see cref="StayCurrency"/>, EUR or CHF): the amounts below are in it, as the charge and its refund are.</param>
/// <param name="PaidAmount">Collected by Stripe so far (refunds not taken out).</param>
/// <param name="AlreadyRefundedAmount">Already refunded or being refunded before now (the host may have refunded part of it).</param>
/// <param name="RefundPercent">The share of the price of the stay the policy grants now, 0–100.</param>
/// <param name="RefundAmount">What the guest gets back if they cancel now: the policy's share plus the tourist tax, within what is still refundable.</param>
/// <param name="RetainedAmount">What the host keeps of what is still refundable.</param>
/// <param name="RetainedBecause"><c>policy</c> when <paramref name="RetainedAmount"/> is more than zero, otherwise <c>null</c>.</param>
/// <param name="Policy">The cancellation policy of the stay; <c>null</c> when the property has none.</param>
/// <param name="FullRefundUntil">Last day with the full refund (Europe/Rome, included); <c>null</c> when the policy has no such window.</param>
/// <param name="PartialRefundPercent">The share of the partial window; <c>null</c> when there is none.</param>
/// <param name="PartialRefundUntil">Last day with the partial refund (Europe/Rome, included); <c>null</c> when there is none.</param>
/// <param name="Stay">The property and the dates.</param>
/// <param name="Outcome">Only for a booking that is already cancelled: the refund of that cancellation and where it stands.</param>
/// <param name="GeneratedAt">When the figures were worked out (UTC).</param>
public sealed record GuestCancellationPreview(
    bool Cancellable,
    string? Reason,
    string Kind,
    string Currency,
    decimal PaidAmount,
    decimal AlreadyRefundedAmount,
    decimal RefundPercent,
    decimal RefundAmount,
    decimal RetainedAmount,
    string? RetainedBecause,
    GuestCancellationPolicyInfo? Policy,
    DateOnly? FullRefundUntil,
    decimal? PartialRefundPercent,
    DateOnly? PartialRefundUntil,
    GuestCancellationStay Stay,
    GuestCancellationOutcome? Outcome,
    DateTime GeneratedAt);

public sealed class GuestRefundChangedException(GuestCancellationPreview current)
    : DomainConflictException(GuestCancellationErrorCodes.RefundChanged, "GuestRefundChanged")
{
    public GuestCancellationPreview Current { get; } = current;
}
