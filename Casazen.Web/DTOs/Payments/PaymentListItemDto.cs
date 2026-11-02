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

    /// <summary>The code the guest knows, <c>XXXXX-XXXXX</c>, of the booking this payment is for.</summary>
    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    /// <summary>First and last name of the guest of the booking.</summary>
    public string GuestName { get; init; } = string.Empty;

    /// <summary>Gross amount, in <see cref="Currency"/>.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of <see cref="Currency"/>.</summary>
    public long AmountCents { get; init; }

    /// <summary>What was refunded so far, in <see cref="Currency"/>.</summary>
    public decimal RefundedAmount { get; init; }

    public long RefundedAmountCents { get; init; }

    /// <summary>
    /// ISO 4217 code of the property of the booking (EUR or CHF, <see cref="StayCurrency"/>): a payment has no currency column of its own,
    /// it is charged and refunded in the one of its property.
    /// </summary>
    public string Currency { get; init; } = StayCurrency.Default;

    /// <summary><c>Pending</c>, <c>Processing</c>, <c>Completed</c>, <c>Failed</c>, <c>Refunded</c>, <c>PartiallyRefunded</c> or <c>Canceled</c>.</summary>
    public PaymentStatus Status { get; init; }

    /// <summary><c>CreditCard</c>, <c>BankTransfer</c>, <c>PayPal</c>, <c>ApplePay</c>, <c>GooglePay</c> or <c>CashOnArrival</c>.</summary>
    public PaymentMethod Method { get; init; }

    public string TransactionId { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>The Stripe PaymentIntent the host sees on the payment page; <c>null</c> for a payment that never went through Stripe.</summary>
    public string? StripePaymentIntentId { get; init; }

    /// <summary>When the payment was settled (UTC); <c>null</c> until then.</summary>
    public DateTime? ProcessedAt { get; init; }

    /// <summary>OTA withholding (21%) on the amount, in euros; 0 when none applies.</summary>
    public decimal OtaWithholdingTax { get; init; }

    public long OtaWithholdingTaxCents { get; init; }

    public bool WithholdingTaxApplied { get; init; }

    /// <summary><c>None</c>, <c>AutoOta</c> or <c>Manual</c>.</summary>
    public WithholdingSource WithholdingSource { get; init; }

    public decimal NetAmountAfterWithholding { get; init; }

    public long NetAmountAfterWithholdingCents { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    /// <summary>
    /// The day the stay of the booking starts, <c>yyyy-MM-dd</c> (a calendar day, no time zone); <c>null</c> when the API did not read it.
    /// The web app tells the payments of a coming or current stay from the ones of a past stay by it and by <see cref="BookingCheckOut"/>.
    /// </summary>
    public DateOnly? BookingCheckIn { get; init; }

    /// <summary>The day the stay of the booking ends, <c>yyyy-MM-dd</c>; <c>null</c> when the API did not read it.</summary>
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
