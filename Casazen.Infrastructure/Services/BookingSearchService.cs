using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBookingSearchService"/>
public sealed class BookingSearchService(AppDbContext db) : IBookingSearchService
{
    public async Task<BookingSearchPage> SearchAsync(
        HostScope scope,
        BookingSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);

        var page = Math.Clamp(criteria.Page, 1, BookingSearchCriteria.MaxPage);
        var pageSize = Math.Clamp(criteria.PageSize, 1, BookingSearchCriteria.MaxPageSize);

        var query = Filter(db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId).InScope(scope), criteria);

        // Latest check-in first, then the id: a total order, so a page boundary never repeats or skips a booking.
        var skipped = (page - 1) * pageSize;
        var items = await query
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .OrderByDescending(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .Skip(skipped)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // A page that is not full is the last one, and says how many there are: no second query. A full page, or a page past the
        // end, does not, and the count is asked.
        var isLastPage = items.Count < pageSize && (items.Count > 0 || skipped == 0);
        var total = isLastPage ? skipped + items.Count : await query.CountAsync(cancellationToken);

        return new BookingSearchPage(items, total, page, pageSize);
    }

    private static IQueryable<Booking> Filter(IQueryable<Booking> query, BookingSearchCriteria criteria)
    {
        if (criteria.PropertyId is { } propertyId)
            query = query.Where(b => b.PropertyId == propertyId);
        if (criteria.GuestId is { } guestId)
            query = query.Where(b => b.GuestId == guestId);

        if (criteria.Statuses is { Count: > 0 } statuses)
        {
            var wanted = statuses.Distinct().ToList();
            query = query.Where(b => wanted.Contains(b.Status));
        }

        // Stay dates, as the calendar reads them (HostCalendarRange): midnight UTC of the day, never moved to a time zone. A stay
        // is in the range when it has a day in it, from its arrival day to its departure day, both included.
        if (criteria.From is { } from)
        {
            var firstDay = StayDate(from);
            query = query.Where(b => b.CheckOutDate >= firstDay);
        }

        if (criteria.To is { } to)
        {
            var dayAfter = StayDate(to).AddDays(1);
            query = query.Where(b => b.CheckInDate < dayAfter);
        }

        return Match(query, criteria.Query);
    }

    private static IQueryable<Booking> Match(IQueryable<Booking> query, string? text)
    {
        var term = text?.Trim();
        if (string.IsNullOrEmpty(term))
            return query;

        if (term.Length > BookingSearchCriteria.MaxQueryLength)
            term = term[..BookingSearchCriteria.MaxQueryLength];

        var lower = term.ToLowerInvariant();
        var code = BookingSearchRules.CodeFragment(term);

        if (code is null)
        {
            return query.Where(b =>
                (b.Guest.FirstName + " " + b.Guest.LastName).ToLower().Contains(lower)
                || (b.Guest.LastName + " " + b.Guest.FirstName).ToLower().Contains(lower)
                || b.Guest.Email.ToLower().Contains(lower)
                || b.Property.Name.ToLower().Contains(lower));
        }

        return query.Where(b =>
            (b.Guest.FirstName + " " + b.Guest.LastName).ToLower().Contains(lower)
            || (b.Guest.LastName + " " + b.Guest.FirstName).ToLower().Contains(lower)
            || b.Guest.Email.ToLower().Contains(lower)
            || b.Property.Name.ToLower().Contains(lower)
            || b.BookingCode.Contains(code));
    }

    private static DateTime StayDate(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
