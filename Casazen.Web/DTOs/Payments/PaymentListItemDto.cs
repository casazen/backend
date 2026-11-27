using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Payments;

/// <summary>
/// A payment as <c>GET /api/payments</c> lists it (SR-03): the guest and the property of its booking, the amounts in euros
/// as before (<see cref="Amount"/>, <see cref="RefundedAmount"/>, ..., which the web app reads) and in cents (the
/// <c>*Cents</c> fields, exact, the figures of the new screens), the method and the status by name. It replaces the entity,
/// which came with its whole booking and with the Stripe account of the host: nothing here is internal to the payment
/// provider but the id of the intent the host sees on the payment page.
/// </summary>
public sealed class PaymentListItemDto
{
    public Guid Id { get; init; }

    public Guid BookingId { get; init; }

    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string GuestName { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public decimal RefundedAmount { get; init; }

    public long RefundedAmountCents { get; init; }

    public string Currency { get; init; } = StayCurrency.Default;

    public PaymentStatus Status { get; init; }

    public PaymentMethod Method { get; init; }

    public string TransactionId { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>The Stripe PaymentIntent the host sees on the payment page; <c>null</c> for a payment that never went through Stripe.</summary>
    public string? StripePaymentIntentId { get; init; }

    public DateTime? ProcessedAt { get; init; }

    public decimal OtaWithholdingTax { get; init; }

    public long OtaWithholdingTaxCents { get; init; }

    public bool WithholdingTaxApplied { get; init; }

    public WithholdingSource WithholdingSource { get; init; }

    public decimal NetAmountAfterWithholding { get; init; }

    public long NetAmountAfterWithholdingCents { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public DateOnly? BookingCheckIn { get; init; }

    public DateOnly? BookingCheckOut { get; init; }

    public static PaymentListItemDto From(PaymentListItem item) => new()
    {
        Id = item.Id,
        BookingId = item.BookingId,
        BookingCode = BookingCodes.Format(item.BookingCode),
        PropertyId = item.PropertyId,
        PropertyName = item.PropertyName,
        GuestName = item.GuestName,
        Amount = item.Amount,
        AmountCents = PaymentCashRules.ToCents(item.Amount),
        RefundedAmount = item.RefundedAmount,
        RefundedAmountCents = PaymentCashRules.ToCents(item.RefundedAmount),
        Currency = StayCurrency.Normalize(item.Currency),
        Status = item.Status,
        Method = item.Method,
        TransactionId = item.TransactionId,
        Description = item.Description,
        StripePaymentIntentId = item.StripePaymentIntentId,
        ProcessedAt = item.ProcessedAt,
        OtaWithholdingTax = item.OtaWithholdingTax,
        OtaWithholdingTaxCents = PaymentCashRules.ToCents(item.OtaWithholdingTax),
        WithholdingTaxApplied = item.WithholdingTaxApplied,
        WithholdingSource = item.WithholdingSource,
        NetAmountAfterWithholding = item.NetAmountAfterWithholding,
        NetAmountAfterWithholdingCents = PaymentCashRules.ToCents(item.NetAmountAfterWithholding),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        BookingCheckIn = item.BookingCheckIn is { } checkIn ? DateOnly.FromDateTime(checkIn) : null,
        BookingCheckOut = item.BookingCheckOut is { } checkOut ? DateOnly.FromDateTime(checkOut) : null,
    };
}
