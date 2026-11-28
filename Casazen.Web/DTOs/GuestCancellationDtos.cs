using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed record GuestCancellationPolicyDto(string Slug, string Name, string? Description);

public sealed record GuestCancellationStayDto(string PropertyName, DateOnly CheckInDate, DateOnly CheckOutDate);

public sealed record GuestCancellationOutcomeDto(decimal RefundAmount, string RefundStatus);

/// <summary>
/// What cancelling the booking would do now (DB-08a): <c>GET api/public/guest/bookings/{code}/cancellation</c> and
/// <c>GET api/public/bookings/{id}/cancellation?token=</c>. Every figure comes from the backend, none is worked out in the browser; the
/// <c>refundAmount</c> is the amount the cancellation refunds. No booking code and nothing personal.
/// </summary>
/// <param name="Cancellable">The guest can cancel now from where they are.</param>
/// <param name="Reason">Why not: <c>already_cancelled</c>, <c>stay_started</c>, <c>not_cancellable_status</c>, <c>payment_in_progress</c>.</param>
/// <param name="Kind"><c>cancel</c>, <c>withdrawRequest</c> (a request to pay at the property, not accepted yet), <c>deferredNoCharge</c> (a saved card never charged).</param>
/// <param name="Currency">ISO 4217 code of the property (EUR or CHF): the amounts below are in it.</param>
/// <param name="PaidAmount">Collected by Stripe so far.</param>
/// <param name="AlreadyRefundedAmount">Refunded or being refunded before now.</param>
/// <param name="RefundPercent">The share of the price of the stay the policy grants now, 0-100 (the tourist tax comes back in full besides).</param>
/// <param name="RefundAmount">What the guest gets back if they cancel now.</param>
/// <param name="RetainedAmount">What the host keeps.</param>
/// <param name="RetainedBecause"><c>policy</c> when something is kept.</param>
/// <param name="Policy">The cancellation policy of the stay, <c>null</c> when the property has none.</param>
/// <param name="FullRefundUntil">Last day (Europe/Rome, included) with the full refund; <c>null</c> when the policy has none.</param>
/// <param name="PartialRefundPercent">The share of the partial window; <c>null</c> when there is none.</param>
/// <param name="PartialRefundUntil">Last day (Europe/Rome, included) with the partial refund; <c>null</c> when there is none.</param>
/// <param name="Stay">The property and the dates.</param>
/// <param name="Outcome">Only for a booking already cancelled: its refund and where it stands.</param>
/// <param name="GeneratedAt">When the figures were worked out (UTC).</param>
public sealed record GuestCancellationPreviewDto(
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
    GuestCancellationPolicyDto? Policy,
    DateOnly? FullRefundUntil,
    decimal? PartialRefundPercent,
    DateOnly? PartialRefundUntil,
    GuestCancellationStayDto Stay,
    GuestCancellationOutcomeDto? Outcome,
    DateTime GeneratedAt)
{
    public static GuestCancellationPreviewDto From(GuestCancellationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new GuestCancellationPreviewDto(
            preview.Cancellable,
            preview.Reason,
            preview.Kind,
            preview.Currency,
            preview.PaidAmount,
            preview.AlreadyRefundedAmount,
            preview.RefundPercent,
            preview.RefundAmount,
            preview.RetainedAmount,
            preview.RetainedBecause,
            preview.Policy is { } policy ? new GuestCancellationPolicyDto(policy.Slug, policy.Name, policy.Description) : null,
            preview.FullRefundUntil,
            preview.PartialRefundPercent,
            preview.PartialRefundUntil,
            new GuestCancellationStayDto(preview.Stay.PropertyName, preview.Stay.CheckInDate, preview.Stay.CheckOutDate),
            preview.Outcome is { } outcome ? new GuestCancellationOutcomeDto(outcome.RefundAmount, outcome.RefundStatus) : null,
            DateTime.SpecifyKind(preview.GeneratedAt, DateTimeKind.Utc));
    }
}

public sealed class GuestCancelRequest
{
    public bool? Confirm { get; set; }

    public decimal? ExpectedRefundAmount { get; set; }
}

public sealed class GuestLinkCancelRequest
{
    public decimal? ExpectedRefundAmount { get; set; }
}

/// <summary>Answer of the cancellation of the guest area: the booking is cancelled; the refund is <c>pending</c> until Stripe confirms it.</summary>
/// <param name="Status">The booking status, <c>Cancelled</c>.</param>
/// <param name="RefundAmount">What the cancellation decided to refund.</param>
/// <param name="RefundStatus"><c>none</c>, <c>pending</c>, <c>confirmed</c> or <c>failed</c>.</param>
public sealed record GuestCancelResponse(string Status, decimal RefundAmount, string RefundStatus)
{
    public static GuestCancelResponse From(GuestCancellationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new GuestCancelResponse(result.Booking.Status.ToString(), result.RefundAmount, result.RefundStatus);
    }
}

public sealed record GuestLinkCancelResponse(Guid BookingId, string Status, decimal RefundAmount, string RefundStatus)
{
    public static GuestLinkCancelResponse From(GuestCancellationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new GuestLinkCancelResponse(result.Booking.Id, result.Booking.Status.ToString(), result.RefundAmount, result.RefundStatus);
    }
}
