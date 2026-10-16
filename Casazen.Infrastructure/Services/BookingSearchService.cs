using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBookingSearchService"/>
/// <remarks>
/// A page costs one query for the bookings (with their property and guest), a second for the total only when the page is full or past
/// the end, and, for what a row says besides the booking (SR-03b), one for the payments and one for the Alloggiati reports of the
/// bookings of the page: never a query per booking. The counters of the chips are one aggregate query.
/// </remarks>
public sealed class BookingSearchService(
    AppDbContext db,
    IConfiguration? configuration = null,
    TimeProvider? timeProvider = null) : IBookingSearchService
{
    private readonly IConfiguration _configuration = configuration ?? new ConfigurationBuilder().Build();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<BookingSearchPage> SearchAsync(
        HostScope scope,
        BookingSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);

        var page = Math.Clamp(criteria.Page, 1, BookingSearchCriteria.MaxPage);
        var pageSize = Math.Clamp(criteria.PageSize, 1, BookingSearchCriteria.MaxPageSize);
        var (today, nowUtc, cutoff) = Now();

        var query = Filter(db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId).InScope(scope), criteria, nowUtc)
            .Where(BookingListRules.InView(criteria.View, today, cutoff));

        // The latest check-in first unless the caller says otherwise, then the id: a total order, so a page boundary never repeats or
        // skips a booking.
        var skipped = (page - 1) * pageSize;
        var items = await Order(query.Include(b => b.Property).Include(b => b.Guest), criteria.Sort)
            .Skip(skipped)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // A page that is not full is the last one, and says how many there are: no second query. A full page, or a page past the
        // end, does not, and the count is asked.
        var isLastPage = items.Count < pageSize && (items.Count > 0 || skipped == 0);
        var total = isLastPage ? skipped + items.Count : await query.CountAsync(cancellationToken);

        return new BookingSearchPage(items, total, page, pageSize)
        {
            Details = await DetailsOfAsync(scope, items, criteria.IncludePaidAmount, today, cancellationToken),
        };
    }

    public async Task<BookingCounts> CountAsync(
        HostScope scope,
        BookingSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);

        var (today, nowUtc, cutoff) = Now();
        var query = Filter(db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId).InScope(scope), criteria, nowUtc);

        // One group for the whole set: COUNT(*) and a COUNT(*) FILTER per chip, the chips being the very predicates of the list.
        var counts = await query
            .GroupBy(_ => 1)
            .Select(CountsSelector(today, cutoff))
            .FirstOrDefaultAsync(cancellationToken);
        return counts ?? new BookingCounts(0, 0, 0, 0, 0, 0);
    }

    private (DateTime Today, DateTime NowUtc, HoldExpiryCutoff Cutoff) Now()
    {
        var now = _clock.GetUtcNow();
        var nowUtc = now.UtcDateTime;
        return (RomeCalendar.TodayAt(now), nowUtc, CheckoutHolds.CutoffAt(nowUtc, CheckoutHolds.GetTtlMinutes(_configuration)));
    }

    // g => new BookingCounts(g.Count(), g.Count(chip 1), ...): built from the predicates of the chips, so the counters and the lists
    // of the chips cannot disagree. EF turns each Count(predicate) into a COUNT(*) FILTER (WHERE ...).
    private static Expression<Func<IGrouping<int, Booking>, BookingCounts>> CountsSelector(DateTime today, HoldExpiryCutoff cutoff)
    {
        var group = Expression.Parameter(typeof(IGrouping<int, Booking>), "g");
        var countAll = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(Booking));
        var countWhere = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Booking));

        Expression CountOf(BookingListView view) =>
            Expression.Call(countWhere, group, BookingListRules.InView(view, today, cutoff));

        var constructor = typeof(BookingCounts).GetConstructors().Single();
        var body = Expression.New(
            constructor,
            Expression.Call(countAll, group),
            CountOf(BookingListView.ToConfirm),
            CountOf(BookingListView.Upcoming),
            CountOf(BookingListView.InProgress),
            CountOf(BookingListView.Completed),
            CountOf(BookingListView.Cancelled));
        return Expression.Lambda<Func<IGrouping<int, Booking>, BookingCounts>>(body, group);
    }

    // The orders of the list. Without one: the latest check-in first, as GET /api/bookings always was. The id closes every order.
    private static IOrderedQueryable<Booking> Order(IQueryable<Booking> query, BookingSort? sort)
    {
        var ordered = (sort?.Key ?? BookingSortKey.CheckIn, sort?.Descending ?? true) switch
        {
            (BookingSortKey.CheckIn, false) => query.OrderBy(b => b.CheckInDate),
            (BookingSortKey.CheckIn, true) => query.OrderByDescending(b => b.CheckInDate),
            (BookingSortKey.CreatedAt, false) => query.OrderBy(b => b.CreatedAt),
            (BookingSortKey.CreatedAt, true) => query.OrderByDescending(b => b.CreatedAt),
            (BookingSortKey.Total, false) => query.OrderBy(b => b.TotalPrice),
            (BookingSortKey.Total, true) => query.OrderByDescending(b => b.TotalPrice),
            (BookingSortKey.Status, false) => query.OrderBy(b => b.Status),
            (BookingSortKey.Status, true) => query.OrderByDescending(b => b.Status),
            (BookingSortKey.Guest, false) => query.OrderBy(b => b.Guest.LastName.ToLower()).ThenBy(b => b.Guest.FirstName.ToLower()),
            (BookingSortKey.Guest, true) =>
                query.OrderByDescending(b => b.Guest.LastName.ToLower()).ThenByDescending(b => b.Guest.FirstName.ToLower()),
            _ => query.OrderByDescending(b => b.CheckInDate),
        };
        return ordered.ThenBy(b => b.Id);
    }

    // The filters of the criteria but the chip, which the list adds and the counters of the chips replace.
    private IQueryable<Booking> Filter(IQueryable<Booking> query, BookingSearchCriteria criteria, DateTime nowUtc)
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

        if (criteria.Channels is { Count: > 0 } channels)
        {
            var sources = BookingListRules.SourcesOf(channels).ToList();
            query = query.Where(b => sources.Contains(b.Source));
        }

        if (criteria.CheckInStates is { Count: > 0 } states)
            query = query.Where(CheckInStateIn(states.Distinct().ToList(), nowUtc));

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

        // The arrival day only (SR-03b): the check-in date, same convention.
        if (criteria.ArrivalFrom is { } arrivalFrom)
        {
            var firstArrival = StayDate(arrivalFrom);
            query = query.Where(b => b.CheckInDate >= firstArrival);
        }

        if (criteria.ArrivalTo is { } arrivalTo)
        {
            var dayAfterArrival = StayDate(arrivalTo).AddDays(1);
            query = query.Where(b => b.CheckInDate < dayAfterArrival);
        }

        return Match(query, criteria.Query);
    }

    // The stays whose online check-in link is in one of the states of GET /api/dashboard/today (HostTodayService.StateOf, from the
    // link alone): a submitted link wins; otherwise the latest link tells. The latest is the newest created, the id as the tie-break.
    private Expression<Func<Booking, bool>> CheckInStateIn(IReadOnlyCollection<HostTodayCheckInState> states, DateTime nowUtc)
    {
        Expression<Func<Booking, bool>> submitted = b => db.GuestCheckInSessions.Any(s =>
            s.BookingId == b.Id
            && (s.Status == GuestCheckInSessionStatus.Completo || s.Status == GuestCheckInSessionStatus.AlloggiatiInviato));
        Expression<Func<Booking, bool>> noLink = b => !db.GuestCheckInSessions.Any(s => s.BookingId == b.Id);
        Expression<Func<Booking, bool>> latestEmailFailed = b => db.GuestCheckInSessions
            .Where(s => s.BookingId == b.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Status == GuestCheckInSessionStatus.Inviato
                && s.ExpiresAt >= nowUtc
                && s.LinkEmailStatus == GuestCheckInLinkEmailStatus.Failed)
            .FirstOrDefault();
        Expression<Func<Booking, bool>> latestSent = b => db.GuestCheckInSessions
            .Where(s => s.BookingId == b.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Status == GuestCheckInSessionStatus.Inviato
                && s.ExpiresAt >= nowUtc
                && s.LinkEmailStatus != GuestCheckInLinkEmailStatus.Failed)
            .FirstOrDefault();
        Expression<Func<Booking, bool>> latestInProgress = b => db.GuestCheckInSessions
            .Where(s => s.BookingId == b.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Status == GuestCheckInSessionStatus.InCompilazione && s.ExpiresAt >= nowUtc)
            .FirstOrDefault();
        Expression<Func<Booking, bool>> latestExpired = b => db.GuestCheckInSessions
            .Where(s => s.BookingId == b.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Status == GuestCheckInSessionStatus.Scaduto
                || ((s.Status == GuestCheckInSessionStatus.Inviato || s.Status == GuestCheckInSessionStatus.InCompilazione)
                    && s.ExpiresAt < nowUtc))
            .FirstOrDefault();

        var notSubmitted = BookingListRules.Not(submitted);
        var predicates = new List<Expression<Func<Booking, bool>>>();
        foreach (var state in states)
        {
            predicates.Add(state switch
            {
                HostTodayCheckInState.Completed => submitted,
                HostTodayCheckInState.Sent => BookingListRules.And(notSubmitted, latestSent),
                HostTodayCheckInState.InProgress => BookingListRules.And(notSubmitted, latestInProgress),
                HostTodayCheckInState.Expired => BookingListRules.And(notSubmitted, latestExpired),
                // No usable link: none was issued, or the email of the latest failed and left the guest with nothing to open.
                _ => BookingListRules.And(notSubmitted, BookingListRules.Or(noLink, latestEmailFailed)),
            });
        }

        return BookingListRules.AnyOf(predicates);
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

    // What each row says besides the booking (SR-03b): how much of the total came in, and where the Alloggiati communication stands.
    // Two reads for the whole page.
    internal async Task<IReadOnlyDictionary<Guid, BookingRowDetails>> DetailsOfAsync(
        HostScope scope,
        IReadOnlyList<Booking> bookings,
        bool includePaidAmount,
        DateTime todayInRome,
        CancellationToken cancellationToken)
    {
        if (bookings.Count == 0)
            return new Dictionary<Guid, BookingRowDetails>();

        var ids = bookings.Select(b => b.Id).ToList();
        var payments = (await db.Payments
                .AsNoTracking()
                .Where(p => p.OrgId == scope.OrgId && ids.Contains(p.BookingId))
                .Select(p => new { p.BookingId, p.Status, p.Amount, p.RefundedAmount })
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.BookingId, p => new BookingPaymentFact(p.Status, p.Amount, p.RefundedAmount));
        var reports = (await db.AlloggiatiWebReports
                .AsNoTracking()
                .Where(r => r.OrgId == scope.OrgId && ids.Contains(r.BookingId))
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.BookingId);

        return bookings.ToDictionary(
            booking => booking.Id,
            booking =>
            {
                var facts = payments[booking.Id].ToList();
                // A communication exists for a stay that is confirmed; a request, a hold or a cancelled booking has none.
                AlloggiatiWebStatus? alloggiati = booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut
                    ? AlloggiatiStatusRules.Effective(
                        AlloggiatiStatusRules.SlotReport(reports[booking.Id].ToList(), booking.GuestId)?.Status,
                        booking.CheckInDate,
                        todayInRome)
                    : null;
                return new BookingRowDetails(
                    BookingListRules.PaymentStateOf(booking.TotalPrice, facts),
                    includePaidAmount ? BookingListRules.PaidAmount(facts) : null,
                    alloggiati);
            });
    }

    private static DateTime StayDate(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
