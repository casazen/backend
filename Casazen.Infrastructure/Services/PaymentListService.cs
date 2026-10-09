using Casazen.Core.Authorization;
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

        if (criteria.To is { } to)
        {
            var end = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) < end);
        }

        return await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Select(p => new PaymentListItem(
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
                p.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}
