using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.Events;

/// <summary>
/// The payload of <c>PaymentSucceeded</c> (E08/F01/US01/T04, ADR-022-R21): a payment of a booking became paid. Ids, the amount, the currency and
/// the method only: never the payer (name, card, address). A consumer reads the rest from the payment by <c>EntityId</c>, in its own org.
/// </summary>
/// <param name="PaymentId">The payment (also the <c>EntityId</c> of the event).</param>
/// <param name="BookingId">The booking it pays.</param>
/// <param name="Amount">The amount paid.</param>
/// <param name="Currency">The currency of the stay (ISO code).</param>
/// <param name="Method">How it was paid, written by name.</param>
public sealed record PaymentEventPayload(
    Guid PaymentId,
    Guid BookingId,
    decimal Amount,
    string Currency,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PaymentMethod Method)
{
    /// <summary>The payload of <paramref name="payment"/> as it is now, in <paramref name="currency"/> (the property's when not given).</summary>
    public static PaymentEventPayload Of(Payment payment, string? currency)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new PaymentEventPayload(payment.Id, payment.BookingId, payment.Amount, StayCurrency.Normalize(currency), payment.Method);
    }
}

/// <summary>
/// The payload of <c>PaymentRefunded</c> (E12/F01/US01/T03, ADR-022-R21): a refund of a payment of a booking succeeded. Ids and the amount only: never the payer. A consumer reads
/// the rest from the payment and the booking, in its own org.
/// </summary>
/// <param name="PaymentId">The payment (also the <c>EntityId</c> of the event).</param>
/// <param name="BookingId">The booking it pays.</param>
/// <param name="RefundedAmount">What was refunded of the payment in all, once this refund succeeded (the currency of the stay).</param>
public sealed record PaymentRefundedPayload(Guid PaymentId, Guid BookingId, decimal RefundedAmount);
