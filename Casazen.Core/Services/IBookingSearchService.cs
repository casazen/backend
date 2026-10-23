using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The bookings of the host as a list that holds up with many of them (SR-03, <c>GET /api/bookings/search</c>): by days, status
/// and text, a page at a time in a fixed order, with the total. Limited to the caller's <see cref="HostScope"/> in SQL. Since
/// SR-03b it is the one list of the bookings: <c>GET /api/bookings</c> (PC-14) is this search with the two filters it has (the
/// property and the guest), so the order, the scope and the answer are one rule (SR-03-FU7). It adds, besides the days, status and
/// text, the arrival days, the chips of the list (<see cref="BookingListView"/>), the channel, the state of the online check-in
/// link and the order (<see cref="BookingSort"/>), and says for each booking how much has been paid and where its Alloggiati
/// communication stands (<see cref="BookingRowDetails"/>).
/// </summary>
public interface IBookingSearchService
{
    /// <summary>
    /// One page of the bookings of <paramref name="scope"/> that satisfy <paramref name="criteria"/>, with their property and
    /// guest, the latest check-in first (or in the order of <see cref="BookingSearchCriteria.Sort"/>) and the id as the last
    /// criterion: the same booking is never on two pages, nor on none, while the data do not change. One query for the page, and
    /// a second for the total only when the page is full or past the end (a page that is not full is the last one and counts
    /// itself), and two for what the rows say (payments, Alloggiati reports) for the whole page: never a query per booking.
    /// </summary>
    Task<BookingSearchPage> SearchAsync(
        HostScope scope,
        BookingSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many bookings fall in each chip (<see cref="BookingListView"/>) among those that satisfy <paramref name="criteria"/>, whatever
    /// their <see cref="BookingSearchCriteria.View"/> (SR-03b): the same filters as <see cref="SearchAsync"/> without the chip, the page
    /// and the order, counted in one aggregate query. A booking is in at most one chip (<see cref="BookingListRules.InView"/>), so the
    /// chips add up to at most <see cref="BookingCounts.All"/>.
    /// </summary>
    Task<BookingCounts> CountAsync(
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
/// <param name="ArrivalFrom">First arrival day (SR-03b, the check-in date, included): the stays that arrive from it on. Not <paramref name="From"/>, which keeps the stays that have a day in the range.</param>
/// <param name="ArrivalTo">Last arrival day (SR-03b, the check-in date, included): the stays that arrive up to it.</param>
/// <param name="View">The chip (SR-03b): <see cref="BookingListView.All"/> is no filter; the others are <see cref="BookingListRules.InView"/>.</param>
/// <param name="Channels">Only these channels (SR-03b); null or empty = every channel.</param>
/// <param name="CheckInStates">
/// Only the stays whose online check-in link is in one of these states (SR-03b, the states of <c>GET /api/dashboard/today</c>:
/// <see cref="HostTodayCheckInState"/>, from the link of the stay alone); null or empty = every state.
/// </param>
/// <param name="Sort">The order (SR-03b); null = the latest check-in first. The booking id is always the last criterion.</param>
/// <param name="IncludePaidAmount">
/// Also give the money that came in for each booking (<see cref="BookingRowDetails.PaidAmount"/>). It is payment data: the web layer
/// sets it only for a caller who may read payments, so the default is to leave it out; the state of the payment is always given.
/// </param>
public sealed record BookingSearchCriteria(
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyCollection<BookingStatus>? Statuses = null,
    string? Query = null,
    Guid? PropertyId = null,
    Guid? GuestId = null,
    int Page = 1,
    int PageSize = BookingSearchCriteria.DefaultPageSize,
    DateOnly? ArrivalFrom = null,
    DateOnly? ArrivalTo = null,
    BookingListView View = BookingListView.All,
    IReadOnlyCollection<BookingChannel>? Channels = null,
    IReadOnlyCollection<HostTodayCheckInState>? CheckInStates = null,
    BookingSort? Sort = null,
    bool IncludePaidAmount = false)
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
public sealed record BookingSearchPage(IReadOnlyList<Booking> Items, int TotalCount, int Page, int PageSize)
{
    /// <summary>What each row adds to its booking (SR-03b), by booking id: every booking of <see cref="Items"/> has one.</summary>
    public IReadOnlyDictionary<Guid, BookingRowDetails> Details { get; init; } = new Dictionary<Guid, BookingRowDetails>();
}

/// <summary>What a row of the booking list says about a booking besides the booking itself (SR-03b).</summary>
/// <param name="PaymentState">How much of the total has come in.</param>
/// <param name="PaidAmount">The money that came in, in euros (<see cref="BookingListRules.PaidAmount"/>); null when the caller may not read payments.</param>
/// <param name="AlloggiatiStatus">
/// Where the Alloggiati Web communication of a stay stands, as the host reads it (<see cref="Casazen.Core.Regulatory.AlloggiatiStatusRules.Effective"/>);
/// null for a booking that has none (pending or cancelled).
/// </param>
public sealed record BookingRowDetails(BookingPaymentState PaymentState, decimal? PaidAmount, AlloggiatiWebStatus? AlloggiatiStatus);

/// <summary>The bookings in each chip of the list (SR-03b).</summary>
/// <param name="All">Every booking that satisfies the criteria.</param>
public sealed record BookingCounts(int All, int ToConfirm, int Upcoming, int InProgress, int Completed, int Cancelled);
