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

    /// <summary>
    /// One page of the same list, in the same order (SR-03b): the same payments as <see cref="ListAsync"/> cut into pages of
    /// <paramref name="pageSize"/> from page <paramref name="page"/> (1-based), with the total. The numbers are brought within the
    /// limits (<see cref="PaymentListPage.MaxPageSize"/>, <see cref="PaymentListPage.MaxPage"/>); the order is total (newest first, the
    /// id as the tie-break), so a payment is never on two pages nor on none. One query for the page, and a second for the total only
    /// when the page is full or past the end.
    /// </summary>
    Task<PaymentListPage> ListPageAsync(
        HostScope scope,
        PaymentListCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>A page of the payment list (SR-03b).</summary>
/// <param name="Items">The payments of the page.</param>
/// <param name="TotalCount">All the payments that satisfy the criteria, on every page.</param>
/// <param name="Page">The page taken (1-based) after bringing the request within the limits.</param>
/// <param name="PageSize">The size taken after bringing the request within the limits.</param>
public sealed record PaymentListPage(IReadOnlyList<PaymentListItem> Items, int TotalCount, int Page, int PageSize)
{
    /// <summary>The page size when the caller asks for pages without saying how big (as the other lists of the host).</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The most payments a page may hold.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The highest page number taken: the order is fixed, a number past the end is just an empty page.</summary>
    public const int MaxPage = 100_000;
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

/// <summary>A payment as the list shows it: amounts as stored, in the currency of the property, the guest and the property named.</summary>
/// <param name="BookingCode">The stored form of the booking code (<see cref="BookingCodes.Format"/> shows it as <c>XXXXX-XXXXX</c>).</param>
/// <param name="GuestName">First and last name of the guest of the booking.</param>
/// <param name="Currency">ISO 4217 code of the property (<see cref="StayCurrency"/>): a payment has no currency column, it is charged and refunded in this one.</param>
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
    DateTime UpdatedAt,
    string Currency = StayCurrency.Default);
