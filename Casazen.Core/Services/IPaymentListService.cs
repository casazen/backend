using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The payments of the host as list rows (SR-03, <c>GET /api/payments</c>): with the guest and the property of their booking, of
/// one booking, of one property and/or of a period, newest first. Limited to the caller's <see cref="HostScope"/> in SQL: a
/// caller reaching some properties only never gets the payments of the others, whatever the criteria say. The caller authorizes
/// the property or the booking it asks about before calling (TN-3); this never widens the scope.
/// </summary>
public interface IPaymentListService
{
    /// <summary>One query whatever the number of payments: the payments of the scope that satisfy <paramref name="criteria"/>, newest first (id as the tie-break).</summary>
    Task<IReadOnlyList<PaymentListItem>> ListAsync(
        HostScope scope,
        PaymentListCriteria criteria,
        CancellationToken cancellationToken = default);
}

/// <summary>What to look for; a null field is no filter.</summary>
/// <param name="PropertyId">Only the payments of the stays of this property.</param>
/// <param name="BookingId">Only the payments of this booking.</param>
/// <param name="From">First day (Europe/Rome, included) of the payment date.</param>
/// <param name="To">Last day (Europe/Rome, included) of the payment date.</param>
/// <remarks>
/// The payment date is the day the payment was settled, <see cref="Payment.ProcessedAt"/>, and, for a payment that has none
/// (not settled, or recorded before it existed), the day it was created: the date the fiscal reports use.
/// </remarks>
public sealed record PaymentListCriteria(
    Guid? PropertyId = null,
    Guid? BookingId = null,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>A payment as the list shows it: amounts in euros as stored, the guest and the property named.</summary>
/// <param name="BookingCode">The stored form of the booking code (<see cref="BookingCodes.Format"/> shows it as <c>XXXXX-XXXXX</c>).</param>
/// <param name="GuestName">First and last name of the guest of the booking.</param>
public sealed record PaymentListItem(
    Guid Id,
    Guid BookingId,
    string BookingCode,
    Guid PropertyId,
    string PropertyName,
    string GuestName,
    decimal Amount,
    decimal RefundedAmount,
    PaymentStatus Status,
    PaymentMethod Method,
    string TransactionId,
    string Description,
    string? StripePaymentIntentId,
    DateTime? ProcessedAt,
    decimal OtaWithholdingTax,
    bool WithholdingTaxApplied,
    decimal NetAmountAfterWithholding,
    WithholdingSource WithholdingSource,
    DateTime CreatedAt,
    DateTime UpdatedAt);
