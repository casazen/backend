using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-03b on a real PostgreSQL database, what EF InMemory cannot prove: the SQL the new reads really produce. Each read runs on the
/// data of <see cref="HostScopeScenario"/> and <see cref="HostOverviewScenario"/> twice, on PostgreSQL and on the InMemory reference,
/// and must say the same, for the collaborator limited to one property and for the whole org (the other org's property has stays,
/// links and interventions too: none may appear). The Home overview with its comparison and its chart, the host's day with its
/// interventions, Alloggiati and cleaning, the booking list with its chips, channels, check-in states and orders, the counters of the
/// chips, the pages of payments, and, for all of them, no command per row.
/// </summary>
public class HostOverviewPostgresTests : IAsyncLifetime
{
    private static readonly TimeProvider Clock = HostOverviewScenario.Clock;

    private static readonly string[] Scopes = ["whole-org", "collaborator"];

    private PostgresTestDatabase? _database;
    private HostScopeWorld _pg = null!;
    private AppDbContext _referenceDb = null!;
    private HostScopeWorld _reference = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using (var db = _database.CreateContext())
        {
            await db.Database.MigrateAsync();
            _pg = await HostScopeScenario.SeedAsync(db);
            await HostOverviewScenario.AddAsync(db, _pg);
        }

        _referenceDb = HostScopeScenario.NewInMemoryDb();
        _reference = await HostScopeScenario.SeedAsync(_referenceDb);
        await HostOverviewScenario.AddAsync(_referenceDb, _reference);
    }

    public async Task DisposeAsync()
    {
        if (_referenceDb is not null)
            await _referenceDb.DisposeAsync();
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private static HostScope ScopeOf(HostScopeWorld world, string kind) => kind == "collaborator" ? world.Restricted : world.OrgWide;

    private static HostDashboardService Dashboard(AppDbContext db) => ShortRentReadApiPostgresTests.Dashboard(db);

    private static BookingSearchService Search(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private static string Shape(object value) => ShortRentReadApiPostgresTests.Shape(value);

    // --- The overview of the Home --------------------------------------------------------------------------

    private static readonly (string Name, Func<HostScopeWorld, HostOverviewQuery> Query)[] Overviews =
    [
        ("october", _ => new HostOverviewQuery(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), IncludeMoney: true)),
        ("last-30-days", _ => new HostOverviewQuery(HostDashboardPeriodKind.Last30Days, IncludeMoney: true)),
        ("next-30-days", _ => new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, IncludeMoney: true)),
        ("next-30-days-without-money", _ => new HostOverviewQuery(HostDashboardPeriodKind.Next30Days)),
        ("weeks", _ => new HostOverviewQuery(
            HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 9, 15), To: new DateOnly(2026, 11, 15), IncludeMoney: true)),
        ("months", _ => new HostOverviewQuery(
            HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 12, 31), IncludeMoney: true)),
        ("one-property", world => new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, PropertyId: world.Granted.Id, IncludeMoney: true)),
    ];

    private static object OccupancyShape(HostOverviewOccupancy occupancy) => new
    {
        occupancy.Occupancy.OccupiedNights,
        occupancy.Occupancy.AvailableNights,
        occupancy.Occupancy.ClosedNights,
        occupancy.DeltaPoints,
    };

    internal static object OverviewShape(HostOverview o) => new
    {
        Period = (o.Period.Kind, o.Period.From, o.Period.To),
        o.TodayInRome,
        o.PropertyCount,
        Occupancy = OccupancyShape(o.Occupancy),
        o.Revenue,
        Collected = o.Collected is null ? null : new { o.Collected.Amount, o.Collected.PaymentCount },
        DirectShare = (o.DirectShare.DirectStays, o.DirectShare.Stays),
        o.Upcoming,
        // The soonest arrivals: the ids decide among stays that start the same day, so the three are compared as a set.
        Next = o.NextArrivals.Select(s => (s.PropertyName, s.GuestName, s.CheckInDate, Status: s.Status.ToString(), Source: s.Source.ToString())).Order().ToList(),
        Previous = new
        {
            Period = (o.Previous.Period.Kind, o.Previous.Period.From, o.Previous.Period.To),
            Occupancy = OccupancyShape(o.Previous.Occupancy),
            o.Previous.Revenue,
            Collected = o.Previous.Collected is null ? null : new { o.Previous.Collected.Amount, o.Previous.Collected.PaymentCount },
            DirectShare = (o.Previous.DirectShare.DirectStays, o.Previous.DirectShare.Stays),
        },
        Series = o.Series is null
            ? null
            : new
            {
                o.Series.Unit,
                Buckets = o.Series.Buckets.Select(b => (b.Label, b.From, b.To, b.Amount, b.PreviousAmount, b.IsFuture)).ToList(),
            },
    };

    [PostgresFact]
    public async Task Overview_PostgreSqlSaysWhatTheReferenceSays_ForEveryPeriod_ForTheCollaboratorAndForTheWholeOrg()
    {
        await using var pg = _database!.CreateContext();

        foreach (var scope in Scopes)
        {
            foreach (var (name, query) in Overviews)
            {
                var onPostgres = await Dashboard(pg).GetOverviewAsync(ScopeOf(_pg, scope), query(_pg));
                var onReference = await Dashboard(_referenceDb).GetOverviewAsync(ScopeOf(_reference, scope), query(_reference));

                var expected = Shape(OverviewShape(onReference));
                var actual = Shape(OverviewShape(onPostgres));
                Assert.True(expected == actual, $"{name}/{scope}{Environment.NewLine}reference:  {expected}{Environment.NewLine}PostgreSQL: {actual}");
            }
        }

        // The scenario has something in it: the test would prove nothing on zeroes.
        var org = await Dashboard(pg).GetOverviewAsync(_pg.OrgWide, Overviews.Single(o => o.Name == "next-30-days").Query(_pg));
        var months = await Dashboard(pg).GetOverviewAsync(_pg.OrgWide, Overviews.Single(o => o.Name == "months").Query(_pg));
        var october = await Dashboard(pg).GetOverviewAsync(_pg.OrgWide, Overviews.Single(o => o.Name == "october").Query(_pg));
        var collaborator = await Dashboard(pg).GetOverviewAsync(_pg.Restricted, Overviews.Single(o => o.Name == "next-30-days").Query(_pg));
        Assert.Equal((2, 1), (org.PropertyCount, collaborator.PropertyCount));
        Assert.True(org.Revenue!.Amount > 0 && october.Collected!.PaymentCount > 0);
        Assert.Equal(3, org.NextArrivals.Count);
        Assert.Equal(HostOverviewSeriesUnit.Month, months.Series!.Unit);
        Assert.Equal(2, months.Upcoming.PendingApproval);
        Assert.Equal(org.Revenue.Amount, org.Series!.Buckets.Sum(b => b.Amount));
        // Nothing of the other org, nor of the property the collaborator was not given.
        Assert.DoesNotContain(org.NextArrivals, s => s.PropertyId == _pg.OtherOrgProperty.Id);
        Assert.All(collaborator.NextArrivals, s => Assert.Equal(_pg.Granted.Id, s.PropertyId));
    }

    [PostgresFact]
    public async Task Overview_OnePropertyIsNotFoundOutsideTheScope_AndNeverMoneyWithoutPaymentRead()
    {
        await using var pg = _database!.CreateContext();

        await Assert.ThrowsAsync<NotFoundException>(() => Dashboard(pg).GetOverviewAsync(
            _pg.Restricted, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, PropertyId: _pg.Hidden.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Dashboard(pg).GetOverviewAsync(
            _pg.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, PropertyId: _pg.OtherOrgProperty.Id)));

        var noMoney = await Dashboard(pg).GetOverviewAsync(_pg.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days));

        Assert.Null(noMoney.Revenue);
        Assert.Null(noMoney.Collected);
        Assert.Null(noMoney.Series);
        Assert.Null(noMoney.Previous.Revenue);
        Assert.Null(noMoney.Previous.Collected);
        Assert.True(noMoney.Occupancy.Occupancy.OccupiedNights > 0);
    }

    // --- The host's day ---------------------------------------------------------------------------------

    private static (string Property, string Guest, string? State, bool? DataComplete, string? Alloggiati, long? Deadline, string? Cleaning) StayExtras(HostTodayStay s) => (
        s.Stay.PropertyName,
        s.Stay.GuestName,
        s.CheckIn?.State.ToString(),
        s.CheckIn?.DataComplete,
        s.Alloggiati?.Status.ToString(),
        s.Alloggiati?.DeadlineAt?.Ticks,
        s.Cleaning?.Status.ToString());

    // Instants as ticks, lists whose order the ids decide sorted by what they say (the two worlds have other ids).
    internal static object TodayShape(HostToday t) => new
    {
        Counts = (t.Arrivals.Count, t.Departures.Count, t.Upcoming.Count, t.Approvals.Count, t.Todo.Count),
        Services = new
        {
            t.Services.Count,
            Items = t.Services.Items
                .Select(s => (s.PropertyName, s.Category, Status: s.Status.ToString(), Start: s.ScheduledStart?.Ticks, End: s.ScheduledEnd?.Ticks, s.SupplierName, OfAStay: s.BookingId != null))
                .Order()
                .ToList(),
        },
        Arrivals = t.Arrivals.Items.Select(StayExtras).Order().ToList(),
        Departures = t.Departures.Items.Select(StayExtras).Order().ToList(),
        Upcoming = t.Upcoming.Items.Select(StayExtras).Order().ToList(),
        Todo = t.Todo.Items.Select(i => (
            Action: i.Action.ToString(),
            i.Priority,
            DueAt: i.DueAt?.Ticks,
            i.Label,
            i.PropertyName,
            OfARequest: i.ServiceRequestId != null,
            OfAStay: i.BookingId != null,
            Missing: string.Join(";", i.Missing.Select(m => $"{m.Code}/{m.Field}/{m.Count}")))).Order().ToList(),
    };

    [PostgresFact]
    public async Task Today_PostgreSqlSaysWhatTheReferenceSays_WithTheInterventionsTheAlloggiatiAndTheCleaning()
    {
        await using var pg = _database!.CreateContext();
        var options = new HostTodayOptions(IncludePayments: true);

        foreach (var scope in Scopes)
        {
            var onPostgres = await HostTodayServiceTests.NewService(pg).GetTodayAsync(ScopeOf(_pg, scope), options);
            var onReference = await HostTodayServiceTests.NewService(_referenceDb).GetTodayAsync(ScopeOf(_reference, scope), options);

            var expected = Shape(TodayShape(onReference));
            var actual = Shape(TodayShape(onPostgres));
            Assert.True(expected == actual, $"{scope}{Environment.NewLine}reference:  {expected}{Environment.NewLine}PostgreSQL: {actual}");
            Assert.True(onReference.Services.Count > 0 && onReference.Arrivals.Items.All(s => s.Alloggiati is not null));
            Assert.Contains(onPostgres.Todo.Items, i => i.Action == HostTodoAction.RespondToServiceRequest && i.ServiceRequestId is not null);
            Assert.Contains(onPostgres.Todo.Items, i => i.Action == HostTodoAction.ConfirmFinalAmount && i.ServiceRequestId is not null);
            // Most pressing first.
            Assert.Equal(onPostgres.Todo.Items.Select(i => i.Priority).Order(), onPostgres.Todo.Items.Select(i => i.Priority));
        }
    }

    [PostgresFact]
    public async Task Today_TheCollaboratorLimitedToOnePropertyNeverGetsTheInterventionsOfTheOthers()
    {
        await using var pg = _database!.CreateContext();

        var restricted = await HostTodayServiceTests.NewService(pg).GetTodayAsync(_pg.Restricted, new HostTodayOptions());
        var orgWide = await HostTodayServiceTests.NewService(pg).GetTodayAsync(_pg.OrgWide, new HostTodayOptions());

        Assert.All(restricted.Services.Items, s => Assert.Equal(_pg.Granted.Id, s.PropertyId));
        Assert.All(restricted.Todo.Items.Where(i => i.ServiceRequestId is not null), i => Assert.Equal(_pg.Granted.Id, i.PropertyId));
        Assert.Equal((2, 4), (restricted.Services.Count, orgWide.Services.Count));
        Assert.DoesNotContain(orgWide.Services.Items, s => s.PropertyId == _pg.OtherOrgProperty.Id);
        // The latest cleaning of each departure: the one of the stay that leaves today, whoever asked for it.
        Assert.All(orgWide.Departures.Items, s => Assert.Equal(ServiceRequestStatus.Richiesto, s.Cleaning!.Status));
    }

    // --- The booking list ---------------------------------------------------------------------------------

    private static readonly (string Name, BookingSearchCriteria Criteria)[] Lists =
    [
        ("all", new BookingSearchCriteria()),
        ("to-confirm", new BookingSearchCriteria(View: BookingListView.ToConfirm)),
        ("upcoming", new BookingSearchCriteria(View: BookingListView.Upcoming)),
        ("in-progress", new BookingSearchCriteria(View: BookingListView.InProgress)),
        ("completed", new BookingSearchCriteria(View: BookingListView.Completed)),
        ("cancelled", new BookingSearchCriteria(View: BookingListView.Cancelled)),
        ("airbnb", new BookingSearchCriteria(Channels: [BookingChannel.Airbnb])),
        ("other-and-manual", new BookingSearchCriteria(Channels: [BookingChannel.Other, BookingChannel.Manual])),
        ("direct", new BookingSearchCriteria(Channels: [BookingChannel.Direct])),
        ("link-not-sent", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.NotSent])),
        ("link-sent", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Sent])),
        ("link-in-progress", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.InProgress])),
        ("link-completed", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Completed])),
        ("link-expired", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Expired])),
        ("link-sent-or-expired", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Sent, HostTodayCheckInState.Expired])),
        ("upcoming-not-sent", new BookingSearchCriteria(View: BookingListView.Upcoming, CheckInStates: [HostTodayCheckInState.NotSent])),
        ("arrivals-9-to-12", new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 9), ArrivalTo: new DateOnly(2026, 10, 12))),
        ("arrivals-from-the-11th", new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 11))),
        ("in-progress-by-text", new BookingSearchCriteria(View: BookingListView.InProgress, Query: "trullo")),
        ("sort-check-in", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CheckIn))),
        ("sort-total-desc", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Total, Descending: true))),
        ("sort-status", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Status))),
        ("sort-status-desc", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Status, Descending: true))),
        ("sort-guest", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Guest))),
        ("sort-guest-desc", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Guest, Descending: true))),
        ("sort-created", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CreatedAt))),
        ("sort-created-desc", new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CreatedAt, Descending: true))),
        ("upcoming-by-total", new BookingSearchCriteria(View: BookingListView.Upcoming, Sort: new BookingSort(BookingSortKey.Total))),
    ];

    // The bookings of a page with what each row says, compared as a set (the ids decide among equal keys).
    private static object RowsShape(BookingSearchPage page) => page.Items
        .Select(b => (
            Property: b.Property.Name,
            Guest: b.Guest.FirstName,
            Status: b.Status.ToString(),
            CheckIn: b.CheckInDate.Ticks,
            Payment: page.Details[b.Id].PaymentState.ToString(),
            Paid: page.Details[b.Id].PaidAmount,
            Alloggiati: page.Details[b.Id].AlloggiatiStatus?.ToString()))
        .Order()
        .ToList();

    // The keys of the order, in the order of the page: equal keys may come in any order of ids, the sequence of keys may not differ.
    private static object KeysShape(BookingSearchPage page, BookingSort? sort) => (sort?.Key ?? BookingSortKey.CheckIn) switch
    {
        BookingSortKey.Total => page.Items.Select(b => b.TotalPrice).ToList(),
        BookingSortKey.Status => page.Items.Select(b => (int)b.Status).ToList(),
        BookingSortKey.Guest => page.Items.Select(b => (b.Guest.LastName.ToLowerInvariant(), b.Guest.FirstName.ToLowerInvariant())).ToList(),
        BookingSortKey.CreatedAt => Array.Empty<int>(),
        _ => page.Items.Select(b => b.CheckInDate.Ticks).ToList(),
    };

    [PostgresFact]
    public async Task BookingList_PostgreSqlSaysWhatTheReferenceSays_ForEveryChipChannelCheckInStateAndOrder()
    {
        await using var pg = _database!.CreateContext();

        foreach (var scope in Scopes)
        {
            foreach (var (name, listed) in Lists)
            {
                var criteria = listed with { PageSize = 100, IncludePaidAmount = true };
                var onPostgres = await Search(pg).SearchAsync(ScopeOf(_pg, scope), criteria);
                var onReference = await Search(_referenceDb).SearchAsync(ScopeOf(_reference, scope), criteria);

                Assert.True(
                    (onReference.TotalCount, onReference.Items.Count) == (onPostgres.TotalCount, onPostgres.Items.Count),
                    $"{name}/{scope}: reference {onReference.TotalCount}/{onReference.Items.Count}, PostgreSQL {onPostgres.TotalCount}/{onPostgres.Items.Count}");
                var expectedRows = Shape(RowsShape(onReference));
                var actualRows = Shape(RowsShape(onPostgres));
                Assert.True(expectedRows == actualRows, $"{name}/{scope} rows{Environment.NewLine}reference:  {expectedRows}{Environment.NewLine}PostgreSQL: {actualRows}");
                var expectedKeys = Shape(KeysShape(onReference, criteria.Sort));
                var actualKeys = Shape(KeysShape(onPostgres, criteria.Sort));
                Assert.True(expectedKeys == actualKeys, $"{name}/{scope} order{Environment.NewLine}reference:  {expectedKeys}{Environment.NewLine}PostgreSQL: {actualKeys}");

                // The date a booking was made is the one of the database (the reference's are the clock's): the order is checked on it.
                if (criteria.Sort?.Key == BookingSortKey.CreatedAt)
                {
                    var created = onPostgres.Items.Select(b => b.CreatedAt).ToList();
                    Assert.Equal(criteria.Sort.Descending ? created.OrderByDescending(c => c) : created.Order(), created);
                }
            }
        }

        // The scenario has something in every chip: the test would prove nothing on empty lists.
        foreach (var chip in new[] { BookingListView.ToConfirm, BookingListView.Upcoming, BookingListView.InProgress, BookingListView.Completed, BookingListView.Cancelled })
        {
            var page = await Search(pg).SearchAsync(_pg.Restricted, new BookingSearchCriteria(View: chip, PageSize: 100));
            Assert.NotEmpty(page.Items);
            Assert.All(page.Items, b => Assert.Equal(_pg.Granted.Id, b.PropertyId));
        }
    }

    [PostgresFact]
    public async Task BookingList_APageTakesAnAnyOrderAndNeverRepeatsOrSkipsABooking()
    {
        await using var pg = _database!.CreateContext();
        var service = Search(pg);

        foreach (var sort in new[]
                 {
                     new BookingSort(BookingSortKey.CheckIn), new BookingSort(BookingSortKey.Total, true), new BookingSort(BookingSortKey.Status),
                     new BookingSort(BookingSortKey.Guest), new BookingSort(BookingSortKey.CreatedAt, true),
                 })
        {
            var all = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(PageSize: 100, Sort: sort));
            var seen = new List<Guid>();
            for (var page = 1; page <= 4; page++)
            {
                var result = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(Page: page, PageSize: 5, Sort: sort));
                Assert.Equal(all.TotalCount, result.TotalCount);
                seen.AddRange(result.Items.Select(b => b.Id));
            }

            // 18 bookings of the org (the other org's are not there): four pages of five, the last one of three.
            Assert.Equal(18, all.TotalCount);
            Assert.Equal(all.Items.Select(b => b.Id), seen);
            Assert.Equal(18, seen.Distinct().Count());
        }
    }

    // --- The counters of the chips ------------------------------------------------------------------------

    private static readonly (string Name, BookingSearchCriteria Filters)[] CountedFilters =
    [
        ("everything", new BookingSearchCriteria()),
        ("airbnb", new BookingSearchCriteria(Channels: [BookingChannel.Airbnb])),
        ("link-sent-or-expired", new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Sent, HostTodayCheckInState.Expired])),
        ("arrivals-from-the-11th", new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 11))),
        ("by-text", new BookingSearchCriteria(Query: "casa bianca")),
        ("not-cancelled", new BookingSearchCriteria(Statuses: [BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.CheckedIn, BookingStatus.CheckedOut])),
    ];

    [PostgresFact]
    public async Task BookingCounts_PostgreSqlSaysWhatTheReferenceSays_AndEachCounterIsTheTotalOfItsChip()
    {
        await using var pg = _database!.CreateContext();

        foreach (var scope in Scopes)
        {
            foreach (var (name, filters) in CountedFilters)
            {
                var onPostgres = await Search(pg).CountAsync(ScopeOf(_pg, scope), filters);
                var onReference = await Search(_referenceDb).CountAsync(ScopeOf(_reference, scope), filters);

                Assert.True(onReference == onPostgres, $"{name}/{scope}: reference {onReference}, PostgreSQL {onPostgres}");
                // Each counter is the total of the list of its chip with the same filters, and the chips share the bookings out.
                foreach (var (view, counted) in new[]
                         {
                             (BookingListView.All, onPostgres.All), (BookingListView.ToConfirm, onPostgres.ToConfirm), (BookingListView.Upcoming, onPostgres.Upcoming),
                             (BookingListView.InProgress, onPostgres.InProgress), (BookingListView.Completed, onPostgres.Completed),
                             (BookingListView.Cancelled, onPostgres.Cancelled),
                         })
                {
                    var page = await Search(pg).SearchAsync(ScopeOf(_pg, scope), filters with { View = view, PageSize = 1 });
                    Assert.True(counted == page.TotalCount, $"{name}/{scope}/{view}: counter {counted}, list {page.TotalCount}");
                }

                Assert.Equal(onPostgres.All, onPostgres.ToConfirm + onPostgres.Upcoming + onPostgres.InProgress + onPostgres.Completed + onPostgres.Cancelled);
            }
        }

        var whole = await Search(pg).CountAsync(_pg.OrgWide, new BookingSearchCriteria());
        var restricted = await Search(pg).CountAsync(_pg.Restricted, new BookingSearchCriteria());
        Assert.Equal(new BookingCounts(18, 2, 5, 5, 4, 2), whole);
        Assert.Equal(new BookingCounts(9, 1, 2, 3, 2, 1), restricted);
    }

    // --- The pages of payments ------------------------------------------------------------------------------

    [PostgresFact]
    public async Task PaymentPages_PostgreSqlCutsTheSamePaymentsInTheSameOrder_NeverRepeatingOneOrSkippingOne()
    {
        await using var pg = _database!.CreateContext();
        var service = new PaymentListService(pg);
        var reference = new PaymentListService(_referenceDb);
        var criteria = new PaymentListCriteria();

        foreach (var scope in Scopes)
        {
            var all = await service.ListAsync(ScopeOf(_pg, scope), criteria);
            var allOnReference = await reference.ListAsync(ScopeOf(_reference, scope), criteria);
            Assert.True(allOnReference.Count == all.Count, $"{scope}: reference {allOnReference.Count}, PostgreSQL {all.Count}");

            var seen = new List<Guid>();
            var pages = (all.Count + 4) / 5;
            for (var page = 1; page <= pages; page++)
            {
                var result = await service.ListPageAsync(ScopeOf(_pg, scope), criteria, page, 5);
                Assert.Equal((all.Count, page, 5), (result.TotalCount, result.Page, result.PageSize));
                Assert.Equal(page < pages || all.Count % 5 == 0 ? 5 : all.Count % 5, result.Items.Count);
                seen.AddRange(result.Items.Select(p => p.Id));
            }

            Assert.Equal(all.Select(p => p.Id), seen);
            // A page past the end is empty and still says how many there are, on PostgreSQL as on the reference.
            var past = await service.ListPageAsync(ScopeOf(_pg, scope), criteria, pages + 3, 5);
            var pastOnReference = await reference.ListPageAsync(ScopeOf(_reference, scope), criteria, pages + 3, 5);
            Assert.Empty(past.Items);
            Assert.Equal((all.Count, 0), (past.TotalCount, past.Items.Count));
            Assert.Equal((past.TotalCount, past.Items.Count), (pastOnReference.TotalCount, pastOnReference.Items.Count));
            // What a page holds is what the array holds: the same payments, shown the same.
            var firstPage = await service.ListPageAsync(ScopeOf(_pg, scope), criteria, 1, 100);
            Assert.Equal(all, firstPage.Items);
        }

        // Two payments per property in the scenario and four of this one's: six for the first property, twelve for the org.
        Assert.Equal(6, (await service.ListAsync(_pg.Restricted, criteria)).Count);
        Assert.Equal(12, (await service.ListAsync(_pg.OrgWide, criteria)).Count);
    }

    // --- No command per row ---------------------------------------------------------------------------------

    private AppDbContext NewCountingContext(ShortRentReadApiPostgresTests.CommandCounter counter) => new(
        new DbContextOptionsBuilder<AppDbContext>(_database!.CreateOptions()).AddInterceptors(counter).Options);

    /// <summary>The commands each read sends, by name.</summary>
    private async Task<Dictionary<string, int>> CommandsPerReadAsync()
    {
        var calls = new Dictionary<string, Func<AppDbContext, Task>>
        {
            ["overview"] = db => Dashboard(db).GetOverviewAsync(
                _pg.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, IncludeMoney: true)),
            ["overview-months"] = db => Dashboard(db).GetOverviewAsync(
                _pg.Restricted, new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31), IncludeMoney: true)),
            ["today"] = db => HostTodayServiceTests.NewService(db).GetTodayAsync(_pg.OrgWide, new HostTodayOptions(IncludePayments: true)),
            ["counts"] = db => Search(db).CountAsync(_pg.OrgWide, new BookingSearchCriteria()),
            ["counts-restricted"] = db => Search(db).CountAsync(_pg.Restricted, new BookingSearchCriteria(Channels: [BookingChannel.Airbnb])),
            ["bookings-page"] = db => Search(db).SearchAsync(_pg.OrgWide, new BookingSearchCriteria(PageSize: 10, IncludePaidAmount: true)),
            ["bookings-page-by-link"] = db => Search(db).SearchAsync(
                _pg.Restricted, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.NotSent, HostTodayCheckInState.Completed], PageSize: 10)),
            ["payments-page"] = db => new PaymentListService(db).ListPageAsync(_pg.OrgWide, new PaymentListCriteria(), 1, 5),
        };

        var counts = new Dictionary<string, int>();
        foreach (var (name, call) in calls)
        {
            var counter = new ShortRentReadApiPostgresTests.CommandCounter();
            await using var db = NewCountingContext(counter);
            await call(db);
            counts[name] = counter.Count;
        }

        return counts;
    }

    private async Task AddPropertiesAsync(int from, int to)
    {
        await using var db = _database!.CreateContext();
        for (var i = from; i < to; i++)
        {
            var property = HostScopeScenario.NewProperty(_pg.OrgId, _pg.OwnerUserId, $"Casa {i}");
            db.Properties.Add(property);
            HostScopeScenario.AddDataOf(db, property, _pg.SupplierOrgId);
            HostOverviewScenario.AddTo(db, property, _pg.SupplierOrgId);
        }

        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Reads_TheNumberOfCommands_DoesNotGrowWithTheNumberOfPropertiesAndStays()
    {
        // Thirty more properties first, so the reads that look at the first ones in detail (the cockpit of the day) and the lists that
        // become full (and ask for their total) are what they will be with any number more.
        await AddPropertiesAsync(0, 30);
        var before = await CommandsPerReadAsync();
        Assert.All(before, entry => Assert.True(entry.Value > 0, $"{entry.Key}: no command counted"));

        // Thirty more, each with its stays, payments, links, reports and interventions: a command per property would be thirty more.
        await AddPropertiesAsync(30, 60);
        var after = await CommandsPerReadAsync();

        foreach (var (name, commands) in before)
        {
            Assert.True(
                after[name] <= commands + 2,
                $"{name}: {commands} commands with 30 more properties, {after[name]} with 60");
        }
    }
}
