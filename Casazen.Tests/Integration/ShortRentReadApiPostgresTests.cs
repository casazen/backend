using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-03 on a real PostgreSQL database, what EF InMemory cannot prove. The reads of the short rent API run on the data of
/// <see cref="HostScopeScenario"/> twice, on PostgreSQL (the SQL the code really produces, with the foreign keys, the collations
/// and the dates of the schema) and on the InMemory reference, and must say the same, for the collaborator limited to one
/// property and for the whole org; the search by text takes <c>%</c> and <c>_</c> literally; the pages of the booking list cut
/// the same bookings in the same order and never repeat one; and no read sends a command per row.
/// </summary>
public class ShortRentReadApiPostgresTests : IAsyncLifetime
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private PostgresTestDatabase? _database;
    private HostScopeWorld _pg = null!;
    private AppDbContext _referenceDb = null!;
    private HostScopeWorld _reference = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
        _pg = await HostScopeScenario.SeedAsync(db);

        _referenceDb = HostScopeScenario.NewInMemoryDb();
        _reference = await HostScopeScenario.SeedAsync(_referenceDb);
    }

    public async Task DisposeAsync()
    {
        if (_referenceDb is not null)
            await _referenceDb.DisposeAsync();
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // The shapes are compared as text. Two things would make that text lie: System.Text.Json writes no field by default (every
    // tuple below would be an empty object, and nothing in it compared), and a decimal keeps its scale (PostgreSQL gives 600.00
    // where the reference has 600, the same amount).
    private static readonly JsonSerializerOptions ShapeOptions = new()
    {
        IncludeFields = true,
        Converters = { new DecimalByValueConverter() },
    };

    internal static string Shape(object value) => JsonSerializer.Serialize(value, ShapeOptions);

    private sealed class DecimalByValueConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("0.########", CultureInfo.InvariantCulture));
    }

    internal static HostDashboardService Dashboard(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    // --- The numbers of the Home --------------------------------------------------------------------------

    private static object Figures(HostDashboardFigures figures) => new
    {
        Period = (figures.Period.Kind, figures.Period.From, figures.Period.To),
        Occupancy = (figures.Occupancy.OccupiedNights, figures.Occupancy.AvailableNights, figures.Occupancy.ClosedNights),
        figures.Revenue,
        figures.RevenueStayCount,
        Collected = figures.Collected is null ? null : new { figures.Collected.Amount, figures.Collected.PaymentCount },
        DirectShare = (figures.DirectShare.DirectStays, figures.DirectShare.Stays),
    };

    internal static object KpiShape(HostDashboardKpis kpis) => new
    {
        kpis.PropertyCount,
        Current = Figures(new HostDashboardFigures(
            kpis.Period, kpis.Occupancy, kpis.Revenue, kpis.RevenueStayCount, kpis.Collected, kpis.DirectShare)),
        Previous = kpis.Previous is null ? null : Figures(kpis.Previous),
        Arrivals = kpis.ArrivalsToday.Count,
        Departures = kpis.DeparturesToday.Count,
        Upcoming = kpis.UpcomingCheckIns.Count,
        Recent = kpis.RecentBookings.Count,
    };

    public static TheoryData<HostDashboardPeriodKind, string> PeriodsAndScopes()
    {
        var data = new TheoryData<HostDashboardPeriodKind, string>();
        foreach (var kind in Enum.GetValues<HostDashboardPeriodKind>())
        {
            data.Add(kind, "whole-org");
            data.Add(kind, "collaborator");
        }

        return data;
    }

    private static HostScope ScopeOf(HostScopeWorld world, string kind) => kind == "collaborator" ? world.Restricted : world.OrgWide;

    [PostgresTheory]
    [MemberData(nameof(PeriodsAndScopes))]
    public async Task Kpis_PostgreSqlSaysWhatTheReferenceSays_ForEveryPeriod_WithTheComparisonAndTheCash(HostDashboardPeriodKind kind, string scope)
    {
        var query = new HostDashboardQuery(kind, new DateOnly(2026, 10, 1), null, Compare: true, IncludeCollected: true);
        await using var pg = _database!.CreateContext();

        var onPostgres = await Dashboard(pg).GetKpisAsync(ScopeOf(_pg, scope), query);
        var onReference = await Dashboard(_referenceDb).GetKpisAsync(ScopeOf(_reference, scope), query);

        Assert.Equal(Shape(KpiShape(onReference)), Shape(KpiShape(onPostgres)));
        // The scenario has money in it: the test would prove nothing on zeroes.
        Assert.True(onReference.PropertyCount > 0);
        Assert.NotNull(onPostgres.Collected);
        Assert.NotNull(onPostgres.Previous);
    }

    [PostgresFact]
    public async Task Kpis_OnePropertyOnPostgreSql_SameAsTheReference_AndNotFoundOutsideTheScope()
    {
        await using var pg = _database!.CreateContext();
        var query = (HostScopeWorld world) => new HostDashboardQuery(
            HostDashboardPeriodKind.Next30Days, null, world.Granted.Id, Compare: true, IncludeCollected: true);

        var onPostgres = await Dashboard(pg).GetKpisAsync(_pg.Restricted, query(_pg));
        var onReference = await Dashboard(_referenceDb).GetKpisAsync(_reference.Restricted, query(_reference));

        Assert.Equal(Shape(KpiShape(onReference)), Shape(KpiShape(onPostgres)));
        Assert.Equal(1, onPostgres.PropertyCount);
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(() => Dashboard(pg).GetKpisAsync(
            _pg.Restricted, new HostDashboardQuery(HostDashboardPeriodKind.Next30Days, PropertyId: _pg.Hidden.Id)));
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(() => Dashboard(pg).GetKpisAsync(
            _pg.OrgWide, new HostDashboardQuery(HostDashboardPeriodKind.Next30Days, PropertyId: _pg.OtherOrgProperty.Id)));
    }

    // --- The day -------------------------------------------------------------------------------------

    // Instants as ticks (the kind of a DateTime read from PostgreSQL is not the reference's), lists whose order the ids decide sorted
    // by what they say (the two worlds have other ids).
    internal static object TodayShape(HostToday today) => new
    {
        Arrivals = today.Arrivals.Count,
        Departures = today.Departures.Count,
        Upcoming = today.Upcoming.Items
            .Select(s => (s.Stay.PropertyName, Status: s.Stay.Status.ToString(), Source: s.Stay.Source.ToString(), State: s.CheckIn?.State.ToString(), DataComplete: s.CheckIn?.DataComplete))
            .Order()
            .ToList(),
        Approvals = today.Approvals.Items.Select(a => (a.PropertyName, RespondBy: a.RespondBy.Ticks, a.NumberOfGuests)).Order().ToList(),
        TodoCount = today.Todo.Count,
        Todo = today.Todo.Items.Select(i => (
            Action: i.Action.ToString(),
            i.Priority,
            DueAt: i.DueAt?.Ticks,
            i.PropertyName,
            Missing: string.Join(";", i.Missing.Select(m => $"{m.Code}/{m.Field}/{m.Count}")))).ToList(),
    };

    [PostgresTheory]
    [InlineData("whole-org")]
    [InlineData("collaborator")]
    public async Task Today_PostgreSqlSaysWhatTheReferenceSays(string scope)
    {
        await using var pg = _database!.CreateContext();
        var options = new HostTodayOptions(IncludePayments: true);

        var onPostgres = await HostTodayServiceTests.NewService(pg).GetTodayAsync(ScopeOf(_pg, scope), options);
        var onReference = await HostTodayServiceTests.NewService(_referenceDb).GetTodayAsync(ScopeOf(_reference, scope), options);

        Assert.Equal(Shape(TodayShape(onReference)), Shape(TodayShape(onPostgres)));
        Assert.True(onReference.Todo.Count > 0);
    }

    // --- The booking list ---------------------------------------------------------------------------

    public static TheoryData<string> Searches()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[]
                 {
                     "all", "pending", "pending-and-confirmed", "october", "after-the-12th", "until-the-9th", "one-day", "by-guest",
                     "by-property", "by-email-fragment", "nothing",
                 })
        {
            data.Add(name);
        }

        return data;
    }

    internal static BookingSearchCriteria CriteriaOf(string name) => name switch
    {
        "pending" => new BookingSearchCriteria(Statuses: [BookingStatus.Pending]),
        "pending-and-confirmed" => new BookingSearchCriteria(Statuses: [BookingStatus.Pending, BookingStatus.Confirmed]),
        "october" => new BookingSearchCriteria(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 31)),
        "after-the-12th" => new BookingSearchCriteria(From: new DateOnly(2026, 10, 13)),
        "until-the-9th" => new BookingSearchCriteria(To: new DateOnly(2026, 10, 9)),
        "one-day" => new BookingSearchCriteria(From: new DateOnly(2026, 10, 4), To: new DateOnly(2026, 10, 4)),
        "by-guest" => new BookingSearchCriteria(Query: "ANNA trullo"),
        "by-property" => new BookingSearchCriteria(Query: "casa bianca"),
        "by-email-fragment" => new BookingSearchCriteria(Query: "@example.com"),
        "nothing" => new BookingSearchCriteria(Query: "zzzz-nessuno"),
        _ => new BookingSearchCriteria(),
    };

    [PostgresTheory]
    [MemberData(nameof(Searches))]
    public async Task BookingSearch_PostgreSqlFindsWhatTheReferenceFinds_ForTheCollaboratorAndForTheWholeOrg(string search)
    {
        await using var pg = _database!.CreateContext();
        var criteria = CriteriaOf(search);

        foreach (var scope in new[] { "collaborator", "whole-org" })
        {
            var onPostgres = await new BookingSearchService(pg).SearchAsync(ScopeOf(_pg, scope), criteria);
            var onReference = await new BookingSearchService(_referenceDb).SearchAsync(ScopeOf(_reference, scope), criteria);

            Assert.True(
                (onReference.TotalCount, onReference.Items.Count) == (onPostgres.TotalCount, onPostgres.Items.Count),
                $"{search}/{scope}: reference {onReference.TotalCount}/{onReference.Items.Count}, PostgreSQL {onPostgres.TotalCount}/{onPostgres.Items.Count}");
            // The same bookings in the same order of check-in; the ids decide only among stays that start the same day.
            Assert.Equal(
                onReference.Items.Select(b => (b.CheckInDate.Ticks, Status: b.Status.ToString(), b.Property.Name)).OrderByDescending(b => b.Ticks).ThenBy(b => b.Status).ThenBy(b => b.Name),
                onPostgres.Items.Select(b => (b.CheckInDate.Ticks, Status: b.Status.ToString(), b.Property.Name)).OrderByDescending(b => b.Ticks).ThenBy(b => b.Status).ThenBy(b => b.Name));
        }
    }

    [PostgresFact]
    public async Task BookingSearch_Pages_CutTheSameBookingsInTheSameOrder_NeverRepeatingOne()
    {
        await using var db = _database!.CreateContext();
        // Many stays that start the same day: only the id tells them apart.
        var guest = await db.Guests.FirstAsync(g => g.OrgId == _pg.OrgId);
        db.Bookings.AddRange(Enumerable.Range(0, 37).Select(i => new Booking
        {
            OrgId = _pg.OrgId,
            PropertyId = _pg.Granted.Id,
            GuestId = guest.Id,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            CheckInDate = new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 22, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
        }));
        await db.SaveChangesAsync();
        var service = new BookingSearchService(db);

        var all = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(PageSize: 100));
        var seen = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(Page: page, PageSize: 10));
            Assert.Equal(all.TotalCount, result.TotalCount);
            seen.AddRange(result.Items.Select(b => b.Id));
        }

        Assert.Equal(43, all.TotalCount);
        Assert.Equal(all.Items.Select(b => b.Id), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        // A page past the end is empty and still says how many there are.
        var past = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(Page: 50, PageSize: 10));
        Assert.Empty(past.Items);
        Assert.Equal(43, past.TotalCount);
        // The last page is not full and counts itself.
        var last = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(Page: 5, PageSize: 10));
        Assert.Equal((3, 43), (last.Items.Count, last.TotalCount));
    }

    [PostgresFact]
    public async Task BookingSearch_TextTakesPercentAndUnderscoreLiterally_NotAsWildcards()
    {
        await using var db = _database!.CreateContext();
        var guest = await db.Guests.FirstAsync(g => g.OrgId == _pg.OrgId && g.LastName == _pg.Granted.Name);
        guest.LastName = "Tru_%llo";
        await db.SaveChangesAsync();
        var service = new BookingSearchService(db);

        // "_" and "%" are in that guest's name only; "u_%l" is a piece of it; "t_u" would match "tru" with a wildcard, and does not.
        foreach (var (text, expectedMatch) in new[] { ("_", true), ("%", true), ("u_%l", true), ("t_u", false), ("tr%o", false), ("a%a", false) })
        {
            var page = await service.SearchAsync(_pg.OrgWide, new BookingSearchCriteria(Query: text));
            var found = page.Items.Any(b => b.Guest.LastName == "Tru_%llo");

            Assert.True(found == expectedMatch, $"'{text}': {(found ? "found" : "not found")}, expected {(expectedMatch ? "found" : "not found")}");
            Assert.All(page.Items, b => Assert.Equal("Tru_%llo", b.Guest.LastName));
        }
    }

    // --- The payments --------------------------------------------------------------------------------

    [PostgresTheory]
    [InlineData("all")]
    [InlineData("the-5th")]
    [InlineData("before-the-5th")]
    [InlineData("after-the-5th")]
    [InlineData("by-property")]
    public async Task PaymentList_PostgreSqlSaysWhatTheReferenceSays_ForTheCollaboratorAndForTheWholeOrg(string name)
    {
        await using var pg = _database!.CreateContext();

        foreach (var scope in new[] { "collaborator", "whole-org" })
        {
            var onPostgres = await new PaymentListService(pg).ListAsync(ScopeOf(_pg, scope), CriteriaOfPayments(name, _pg));
            var onReference = await new PaymentListService(_referenceDb).ListAsync(ScopeOf(_reference, scope), CriteriaOfPayments(name, _reference));

            Assert.True(onReference.Count == onPostgres.Count, $"{name}/{scope}: reference {onReference.Count}, PostgreSQL {onPostgres.Count}");
            Assert.Equal(
                onReference.Select(p => (p.PropertyName, p.GuestName, p.Amount, p.RefundedAmount, Status: p.Status.ToString(), Processed: p.ProcessedAt?.Ticks)).Order(),
                onPostgres.Select(p => (p.PropertyName, p.GuestName, p.Amount, p.RefundedAmount, Status: p.Status.ToString(), Processed: p.ProcessedAt?.Ticks)).Order());
        }
    }

    internal static PaymentListCriteria CriteriaOfPayments(string name, HostScopeWorld world) => name switch
    {
        "the-5th" => new PaymentListCriteria(From: new DateOnly(2026, 10, 5), To: new DateOnly(2026, 10, 5)),
        "before-the-5th" => new PaymentListCriteria(To: new DateOnly(2026, 10, 4)),
        "after-the-5th" => new PaymentListCriteria(From: new DateOnly(2026, 10, 6)),
        "by-property" => new PaymentListCriteria(PropertyId: world.Granted.Id),
        _ => new PaymentListCriteria(),
    };

    [PostgresFact]
    public async Task PaymentList_TheCollaboratorLimitedToSomePropertiesNeverGetsTheOthers_WhateverTheCriteria()
    {
        await using var db = _database!.CreateContext();
        var service = new PaymentListService(db);
        var hiddenBooking = await db.Bookings.Where(b => b.PropertyId == _pg.Hidden.Id).Select(b => b.Id).FirstAsync();

        var everything = await service.ListAsync(_pg.Restricted, new PaymentListCriteria());
        var hiddenByProperty = await service.ListAsync(_pg.Restricted, new PaymentListCriteria(PropertyId: _pg.Hidden.Id));
        var hiddenByBooking = await service.ListAsync(_pg.Restricted, new PaymentListCriteria(BookingId: hiddenBooking));
        var wholeOrg = await service.ListAsync(_pg.OrgWide, new PaymentListCriteria());

        Assert.NotEmpty(everything);
        Assert.All(everything, p => Assert.Equal(_pg.Granted.Id, p.PropertyId));
        Assert.Empty(hiddenByProperty);
        Assert.Empty(hiddenByBooking);
        // Two payments per property in the scenario, and none of the other org's.
        Assert.Equal(4, wholeOrg.Count);
        Assert.DoesNotContain(wholeOrg, p => p.PropertyId == _pg.OtherOrgProperty.Id);
    }

    // --- No command per row ---------------------------------------------------------------------------

    /// <summary>Counts the commands a context sends to the server.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private AppDbContext NewCountingContext(CommandCounter counter) => new(
        new DbContextOptionsBuilder<AppDbContext>(_database!.CreateOptions()).AddInterceptors(counter).Options);

    /// <summary>The commands each read sends for the whole org, by name.</summary>
    private async Task<Dictionary<string, int>> CommandsPerReadAsync()
    {
        var calls = new Dictionary<string, Func<AppDbContext, Task>>
        {
            ["kpis"] = db => Dashboard(db).GetKpisAsync(
                _pg.OrgWide, new HostDashboardQuery(HostDashboardPeriodKind.Next30Days, Compare: true, IncludeCollected: true)),
            ["today-stays"] = db => Dashboard(db).GetTodayStaysAsync(_pg.OrgWide),
            ["bookings-search-page"] = db => new BookingSearchService(db).SearchAsync(_pg.OrgWide, new BookingSearchCriteria(Query: "anna", PageSize: 10)),
            ["payments-list"] = db => new PaymentListService(db).ListAsync(_pg.OrgWide, new PaymentListCriteria()),
        };

        var counts = new Dictionary<string, int>();
        foreach (var (name, call) in calls)
        {
            var counter = new CommandCounter();
            await using var db = NewCountingContext(counter);
            await call(db);
            counts[name] = counter.Count;
        }

        return counts;
    }

    [PostgresFact]
    public async Task Reads_TheNumberOfCommands_DoesNotGrowWithTheNumberOfPropertiesAndBookings()
    {
        var before = await CommandsPerReadAsync();
        Assert.All(before, entry => Assert.True(entry.Value > 0, $"{entry.Key}: no command counted"));
        Assert.True(before["bookings-search-page"] <= 2, $"search: {before["bookings-search-page"]} commands");
        Assert.True(before["payments-list"] <= 1, $"payments: {before["payments-list"]} commands");

        // Thirty more properties of the org, each with its stays, payments and feeds.
        await using (var db = _database!.CreateContext())
        {
            for (var i = 0; i < 30; i++)
            {
                var property = HostScopeScenario.NewProperty(_pg.OrgId, _pg.OwnerUserId, $"Casa {i}");
                db.Properties.Add(property);
                HostScopeScenario.AddDataOf(db, property, _pg.SupplierOrgId);
            }

            await db.SaveChangesAsync();
        }

        var after = await CommandsPerReadAsync();

        // The search asks for the total only when its page is full, which it is not with six stays and is with thirty-six.
        Assert.Equal(before.Where(read => read.Key != "bookings-search-page"), after.Where(read => read.Key != "bookings-search-page"));
        Assert.InRange(after["bookings-search-page"], 1, 2);
    }

    [PostgresFact]
    public async Task CockpitMissing_AFewReadsWhateverTheNumberOfStaysAndOfPropertiesToActivate()
    {
        await using (var db = _database!.CreateContext())
        {
            for (var i = 0; i < 30; i++)
            {
                var property = HostScopeScenario.NewProperty(_pg.OrgId, _pg.OwnerUserId, $"Casa {i}");
                db.Properties.Add(property);
                HostScopeScenario.AddDataOf(db, property, _pg.SupplierOrgId);
            }

            await db.SaveChangesAsync();
        }

        ComplianceSummaryResult summary;
        await using (var db = _database!.CreateContext())
        {
            summary = await Casazen.Tests.Unit.Services.ComplianceWizardServiceTests.CreateService(db, Clock).GetSummaryAsync(_pg.OrgWide);
        }

        var counter = new CommandCounter();
        await using var counting = NewCountingContext(counter);
        var described = await ComplianceMissingServiceTests.Service(counting).DescribeAsync(summary);

        Assert.True(summary.PropertiesPending.Count > IComplianceMissingService.MaxDetailedProperties);
        Assert.True(summary.GuestCheckInsIncomplete.Count > 20);
        Assert.All(
            new[] { described.PropertiesPending, described.GuestCheckInsIncomplete, described.TurnoversPending }.SelectMany(s => s.Items),
            item => Assert.NotEmpty(item.Missing));
        // Three reads for the stays and a handful for each of the first properties: the same with 3 stays or with 300.
        Assert.True(counter.Count <= 3 + (IComplianceMissingService.MaxDetailedProperties * 4), $"{counter.Count} commands");
    }
}
