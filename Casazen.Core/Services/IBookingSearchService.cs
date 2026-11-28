using Casazen.Core.Authorization;
using Casazen.Core.Bookings;
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
/// <param name="Origins">
/// Only the bookings that came from one of these origins (E27/F01/US01/T01, NF-123: a portal, the site of the agency, CasaZen's own site, the host,
/// <see cref="BookingOrigins.Matching"/>); null or empty = every origin. It narrows, like every filter: the caller's scope is applied besides it.
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
    bool IncludePaidAmount = false,
    IReadOnlyCollection<BookingOrigin>? Origins = null)
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    public const int MaxPage = 100_000;

    public const int MaxQueryLength = 100;
}

public sealed record BookingSearchPage(IReadOnlyList<Booking> Items, int TotalCount, int Page, int PageSize)
{
    public IReadOnlyDictionary<Guid, BookingRowDetails> Details { get; init; } = new Dictionary<Guid, BookingRowDetails>();
}

/// <summary>What a row of the booking list says about a booking besides the booking itself (SR-03b).</summary>
/// <param name="PaymentState">How much of the total has come in.</param>
/// <param name="PaidAmount">The money that came in, in euros (<see cref="BookingListRules.PaidAmount"/>); null when the caller may not read payments.</param>
/// <param name="AlloggiatiStatus">
/// Where the Alloggiati Web communication of a stay stands, as the host reads it (<see cref="Casazen.Core.Regulatory.AlloggiatiStatusRules.Effective"/>);
/// null for a booking that has none (pending, cancelled or a no-show).
/// </param>
public sealed record BookingRowDetails(BookingPaymentState PaymentState, decimal? PaidAmount, AlloggiatiWebStatus? AlloggiatiStatus);

public sealed record BookingCounts(int All, int ToConfirm, int Upcoming, int InProgress, int Completed, int Cancelled);
