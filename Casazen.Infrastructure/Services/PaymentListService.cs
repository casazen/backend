using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

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

        if (criteria.From is { } from)
        {
            var start = RomeCalendar.StartOfDayUtc(from);
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) >= start);
        }

        if (criteria.To is { } to && to != DateOnly.MaxValue)
        {
            var end = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) < end);
        }

        return query;
    }

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
        p.UpdatedAt,
        p.Booking.Property.Currency,
        p.Booking.CheckInDate,
        p.Booking.CheckOutDate);
}
