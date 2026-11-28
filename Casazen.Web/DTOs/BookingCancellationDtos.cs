using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Payments;

namespace Casazen.Web.DTOs;

public sealed class CancelBookingRequest
{
    [Range(0d, 1_000_000d, ErrorMessage = "RefundAmountInvalid")]
    public decimal? RefundAmount { get; set; }

    [MaxLength(500, ErrorMessage = "RefundReasonTooLong")]
    public string? Reason { get; set; }
}

public sealed record BookingCancellationQuoteDto(
    Guid BookingId,
    BookingStatus Status,
    bool Cancellable,
    string Currency,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal PendingRefundAmount,
    decimal RefundableAmount,
    decimal MinimumRefundAmount,
    CancellationRefundRule Rule,
    DateTime? FreeCancellationUntil,
    string? CancellationPolicyName,
    decimal OfflinePaidAmount,
    bool HasUncollectedIntent,
    bool RequiresRefundDecision)
{
    public static BookingCancellationQuoteDto From(BookingCancellationQuote quote) => new(
        quote.BookingId,
        quote.Status,
        quote.Cancellable,
        quote.Currency,
        quote.PaidAmount,
        quote.RefundedAmount,
        quote.PendingRefundAmount,
        quote.RefundableAmount,
        quote.MinimumRefundAmount,
        quote.Rule,
        quote.FreeCancellationUntil,
        quote.CancellationPolicyName,
        quote.OfflinePaidAmount,
        quote.HasUncollectedIntent,
        quote.RequiresRefundDecision);
}

public sealed record CancelBookingResponse(
    Guid BookingId,
    BookingStatus Status,
    IReadOnlyList<PaymentRefundDto> Refunds,
    int CanceledIntents);
