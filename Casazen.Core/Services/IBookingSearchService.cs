using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The bookings of the host as a list that holds up with many of them (SR-03, <c>GET /api/bookings/search</c>): by days, status
/// and text, a page at a time in a fixed order, with the total. Limited to the caller's <see cref="HostScope"/> in SQL, like
/// <see cref="IBookingService.GetPagedBookingsAsync"/> (<c>GET /api/bookings</c>, PC-14), whose order it keeps and to which it
/// adds the filters.
/// </summary>
public interface IBookingSearchService
{
    /// <summary>
    /// One page of the bookings of <paramref name="scope"/> that satisfy <paramref name="criteria"/>, with their property and
    /// guest, the latest check-in first and the id as the tie-break: the same booking is never on two pages, nor on none, while
    /// the data do not change. One query for the page, and a second for the total only when the page is full or past the end
    /// (a page that is not full is the last one and counts itself): never a query per booking.
    /// </summary>
    Task<BookingSearchPage> SearchAsync(
        HostScope scope,
        BookingSearchCriteria criteria,
        CancellationToken cancellationToken = default);
}

/// <summary>What to look for.</summary>
/// <param name="From">First day (a stay date, included): the stays that have a day from it on, departure day included.</param>
/// <param name="To">Last day (a stay date, included): the stays that have a day up to it, arrival day included.</param>
/// <param name="Statuses">Only these statuses; null or empty = every status.</param>
/// <param name="Query">
/// Text looked for, any case, in the name of the guest (first and last, in either order), in the email of the guest, in the
/// name of the property and, when it can be one, in the booking code (<see cref="BookingSearchRules.CodeFragment"/>).
/// </param>
/// <param name="PropertyId">Only the stays of this property.</param>
/// <param name="GuestId">Only the stays of this guest.</param>
/// <param name="Page">From 1; a smaller number is read as 1.</param>
/// <param name="PageSize">From 1 to <see cref="MaxPageSize"/>; out of range is brought within it.</param>
public sealed record BookingSearchCriteria(
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyCollection<BookingStatus>? Statuses = null,
    string? Query = null,
    Guid? PropertyId = null,
    Guid? GuestId = null,
    int Page = 1,
    int PageSize = BookingSearchCriteria.DefaultPageSize)
{
    /// <summary>Bookings per page when the caller does not say (as the guest list).</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The most bookings a page may hold.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The highest page number taken: the order is fixed, a number past the end is just an empty page.</summary>
    public const int MaxPage = 100_000;

    /// <summary>The longest text looked for; the rest is ignored.</summary>
    public const int MaxQueryLength = 100;
}

/// <summary>A page of bookings, as found.</summary>
/// <param name="Items">The bookings of the page, with their property and guest.</param>
/// <param name="TotalCount">All the bookings that satisfy the criteria, on every page.</param>
/// <param name="Page">The page taken (1-based) after bringing the request within the limits.</param>
/// <param name="PageSize">The size taken after bringing the request within the limits.</param>
public sealed record BookingSearchPage(IReadOnlyList<Booking> Items, int TotalCount, int Page, int PageSize);
