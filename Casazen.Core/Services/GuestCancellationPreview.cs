using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>Where the guest cancels from: it decides which stays can still be cancelled (DB-08a).</summary>
public enum GuestCancellationChannel
{
    /// <summary>
    /// The guest area (<c>api/public/guest/*</c>): the guest proved the booking with its code and email. A stay that has started
    /// (the check-in day began, or the host registered the arrival) is not cancelled here: the guest writes to the host (DB-D3).
    /// </summary>
    GuestArea,

    /// <summary>
    /// The signed one-time link the host emailed (DB-08h): the host decided to send it, so it also works for a stay in progress.
    /// </summary>
    SignedLink,
}

/// <summary>What cancelling means to the guest: the stable <c>kind</c> of a <see cref="GuestCancellationPreview"/>.</summary>
public static class GuestCancellationKinds
{
    /// <summary>A booking that was confirmed: the refund of the policy, if any, goes back to the payment method.</summary>
    public const string Cancel = "cancel";

    /// <summary>A request to pay at the property that the host has not accepted yet: the guest withdraws it, nothing was paid.</summary>
    public const string WithdrawRequest = "withdrawRequest";

    /// <summary>"Paga alla scadenza" with nothing charged yet: the saved card is released, the guest pays nothing.</summary>
    public const string DeferredNoCharge = "deferredNoCharge";
}

/// <summary>Why a booking cannot be cancelled by the guest: the stable <c>reason</c> of a <see cref="GuestCancellationPreview"/>.</summary>
public static class GuestCancellationReasons
{
    public const string AlreadyCancelled = "already_cancelled";
    public const string StayStarted = "stay_started";
    public const string NotCancellableStatus = "not_cancellable_status";
    public const string PaymentInProgress = "payment_in_progress";
}

/// <summary>Stable error codes of the cancellation of the guest (frontend <c>apiErrors.codes.*</c>).</summary>
public static class GuestCancellationErrorCodes
{
    /// <summary>
    /// 409: the refund is not the one the guest was shown (a threshold of the policy was crossed, or a payment changed) and nothing was
    /// cancelled; the answer carries the new preview.
    /// </summary>
    public const string RefundChanged = "refund_changed";

    /// <summary>422: the stay has started; the guest writes to the host.</summary>
    public const string StayStarted = "guest_cancel_stay_started";

    /// <summary>422: the body does not carry the confirmation the guest gave.</summary>
    public const string ConfirmationRequired = "guest_cancel_confirmation_required";
}

/// <summary>The policy of the stay as the guest reads it. The description is the catalog's, and only while the host did not change its windows.</summary>
/// <param name="Slug">Stable identifier of the catalog policy.</param>
/// <param name="Name">Name of the policy (not translated).</param>
/// <param name="Description">The catalog text; <c>null</c> when the host's overrides make it say something else.</param>
public sealed record GuestCancellationPolicyInfo(string Slug, string Name, string? Description);

/// <summary>The stay a preview is about: only what is on the page, nothing personal.</summary>
public sealed record GuestCancellationStay(string PropertyName, DateOnly CheckInDate, DateOnly CheckOutDate);

/// <summary>What happened when a booking was already cancelled: the refund of its cancellation and where it stands.</summary>
/// <param name="RefundAmount">What the cancellation decided to refund (EUR).</param>
/// <param name="RefundStatus"><see cref="GuestCancellationRefundStatuses"/>.</param>
public sealed record GuestCancellationOutcome(decimal RefundAmount, string RefundStatus);

/// <summary>Where the refund of a cancellation stands; never "refunded" before the bank confirms it.</summary>
public static class GuestCancellationRefundStatuses
{
    /// <summary>Nothing to refund (nothing was paid, or the policy refunds nothing).</summary>
    public const string None = "none";

    /// <summary>Started, Stripe has not confirmed it yet.</summary>
    public const string Pending = "pending";

    /// <summary>Every refund of the cancellation was confirmed by Stripe.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Stripe refused a refund; the host sees it and retries.</summary>
    public const string Failed = "failed";

    /// <summary>The status of a set of refunds, as <see cref="Entities.PaymentRefundStatus"/> left them.</summary>
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
/// <param name="Currency">ISO 4217, EUR.</param>
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

/// <summary>
/// The refund the guest was shown is not the one the cancellation would make now (a threshold of the policy was crossed, or a payment
/// changed): nothing was cancelled, <see cref="Current"/> is what the guest sees now and confirms again (DB-08a). 409
/// <see cref="GuestCancellationErrorCodes.RefundChanged"/>.
/// </summary>
public sealed class GuestRefundChangedException(GuestCancellationPreview current)
    : DomainConflictException(GuestCancellationErrorCodes.RefundChanged, "GuestRefundChanged")
{
    /// <summary>The preview as it is now.</summary>
    public GuestCancellationPreview Current { get; } = current;
}
