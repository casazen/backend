using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPaymentListService"/>
/// <remarks>
/// The payments of a deleted property are still listed (<see cref="AppDbContext.SoftDeleteQueryFilter"/> is ignored, as the fiscal
/// reports do): the money that came in stays visible after the property is archived.
/// </remarks>
public sealed class PaymentListService(AppDbContext db) : IPaymentListService
{
    public async Task<IReadOnlyList<PaymentListItem>> ListAsync(
        HostScope scope,
        PaymentListCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);

        return await Ordered(Filter(scope, criteria))
            .Select(ItemProjection)
            .ToListAsync(cancellationToken);
    }

    public async Task<PaymentListPage> ListPageAsync(
        HostScope scope,
        PaymentListCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);

        page = Math.Clamp(page, 1, PaymentListPage.MaxPage);
        pageSize = Math.Clamp(pageSize, 1, PaymentListPage.MaxPageSize);

        var query = Filter(scope, criteria);
        var skipped = (page - 1) * pageSize;
        var items = await Ordered(query)
            .Skip(skipped)
            .Take(pageSize)
            .Select(ItemProjection)
            .ToListAsync(cancellationToken);

        // A page that is not full is the last one, and says how many there are: no second query. A full page, or a page past the
        // end, does not, and the count is asked.
        var isLastPage = items.Count < pageSize && (items.Count > 0 || skipped == 0);
        var total = isLastPage ? skipped + items.Count : await query.CountAsync(cancellationToken);

        return new PaymentListPage(items, total, page, pageSize);
    }

    private IQueryable<Payment> Filter(HostScope scope, PaymentListCriteria criteria)
    {
        var query = db.Payments
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope);

        if (criteria.PropertyId is { } propertyId)
            query = query.Where(p => p.Booking.PropertyId == propertyId);
        if (criteria.BookingId is { } bookingId)
            query = query.Where(p => p.BookingId == bookingId);

        // The days are Europe/Rome calendar days; the payment date is when it was settled, else when it was created (the
        // fiscal reports' rule), compared as an instant.
        if (criteria.From is { } from)
        {
            var start = RomeCalendar.StartOfDayUtc(from);
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) >= start);
        }

        // The last day the calendar has is no upper bound: the day after it cannot be written.
        if (criteria.To is { } to && to != DateOnly.MaxValue)
        {
            var end = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) < end);
        }

        return query;
    }

    // Newest first, then the id: a total order, so a page boundary never repeats or skips a payment.
    private static IOrderedQueryable<Payment> Ordered(IQueryable<Payment> query) =>
        query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id);

    private static readonly Expression<Func<Payment, PaymentListItem>> ItemProjection = p => new PaymentListItem(
        p.Id,
        p.BookingId,
        p.Booking.BookingCode,
        p.Booking.PropertyId,
        p.Booking.Property.Name,
        (p.Booking.Guest.FirstName + " " + p.Booking.Guest.LastName).Trim(),
        p.Amount,
        p.RefundedAmount,
        p.Status,
        p.Method,
        p.TransactionId,
        p.Description,
        p.StripePaymentIntentId,
        p.ProcessedAt,
        p.OtaWithholdingTax,
        p.WithholdingTaxApplied,
        p.NetAmountAfterWithholding,
        p.WithholdingSource,
        p.CreatedAt,
        p.UpdatedAt);
}
