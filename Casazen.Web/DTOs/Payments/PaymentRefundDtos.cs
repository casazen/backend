using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Payments;

/// <summary>Body of <c>POST /api/payments/{id}/refund</c>; without <see cref="Amount"/> everything still refundable.</summary>
public sealed class RefundPaymentRequest
{
    [Range(0.01, 1_000_000, ErrorMessage = "RefundAmountInvalid")]
    public decimal? Amount { get; set; }

    [MaxLength(500, ErrorMessage = "RefundReasonTooLong")]
    public string? Reason { get; set; }
}

/// <summary>
/// One refund as Stripe left it. <see cref="Status"/>: Pending (Stripe or a retry still working), RequiresAction,
/// Succeeded (confirmed by Stripe), Failed, Canceled.
/// </summary>
public sealed record PaymentRefundDto(
    Guid Id,
    Guid PaymentId,
    decimal Amount,
    PaymentRefundStatus Status,
    PaymentRefundOrigin Origin,
    string? FailureReason,
    string? Reason,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    public static PaymentRefundDto From(PaymentRefund refund) => new(
        refund.Id,
        refund.PaymentId,
        refund.Amount,
        refund.Status,
        refund.Origin,
        refund.FailureReason,
        refund.Reason,
        refund.CreatedAt,
        refund.CompletedAt);
}

/// <summary>Refunds of one payment with its amounts (<c>GET /api/payments/{id}/refunds</c>).</summary>
public sealed record PaymentRefundsResponse(
    Guid PaymentId,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal PendingRefundAmount,
    decimal RefundableAmount,
    bool RefundableOnline,
    IReadOnlyList<PaymentRefundDto> Refunds)
{
    public static PaymentRefundsResponse From(Guid paymentId, PaymentRefundSummary summary, IEnumerable<PaymentRefund> refunds) => new(
        paymentId,
        summary.PaidAmount,
        summary.RefundedAmount,
        summary.PendingRefundAmount,
        summary.RefundableAmount,
        summary.RefundableOnline,
        refunds.Select(PaymentRefundDto.From).ToList());
}
