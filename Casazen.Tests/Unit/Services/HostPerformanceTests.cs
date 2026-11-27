using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-96: ADR, RevPAR and the comparison with the same days one year earlier are the Home cut up, never a second rule. For the same days
/// the total of a performance report is the revenue and the occupancy the Home gives, ADR and RevPAR are worked out from them, and the
/// year before is the same calendar days. Numbers worked out by hand, October 2026.
/// </summary>
public class HostPerformanceTests
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Booking Stay(Property house, Guid guestId, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal basePrice) => new()
    {
        OrgId = house.OrgId,
        PropertyId = house.Id,
        GuestId = guestId,
        Status = status,
        Source = source,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
        BasePrice = basePrice,
        TotalPrice = basePrice,
    };

    // A house that has been in the org since 2024, unless said otherwise.
    private static Property NewHouse(Guid orgId, string name, DateTime? createdAt = null)
    {
        var house = HostScopeScenario.NewProperty(orgId, "auth0|titolare-nf96", name);
        house.CreatedAt = createdAt ?? Day(2024, 1, 1);
        return house;
    }

    private sealed record Seeded(
        AppDbContext Db,
        HostDashboardService Service,
        HostScope Scope,
        Guid OrgId,
        Guid GuestId,
        Property Trullo,
        Property CasaBianca,
        Property Paused);

    // Trullo: A 10 to 13 October, direct, 300 (3 nights); C 30 October to 3 November, Airbnb, 400 for 4 nights (2 of them in October: 200);
    // D cancelled, nothing. Casa Bianca: B 8 to 11 October, manual, 300. The paused villa still has its money: E 5 to 8 October, 150. Another
    // org's stay is nobody's here. With the year before: Trullo has a direct stay of 14 to 17 October 2025, 270 for 3 nights.
    private static async Task<Seeded> SeedAsync(bool withPreviousYear)
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = NewHouse(orgId, "Trullo");
        var casaBianca = NewHouse(orgId, "Casa Bianca");
        var paused = NewHouse(orgId, "Villa in pausa");
        paused.IsActive = false;
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-nf96", "Villa Altrove");
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var foreignGuest = new Guest { OrgId = otherOrgId, FirstName = "Luca", LastName = "Neri", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.AddRange(trullo, casaBianca, paused, foreign);
        db.Guests.AddRange(guest, foreignGuest);
        db.Bookings.AddRange(
            Stay(trullo, guest.Id, BookingStatus.Confirmed, BookingSource.Direct, Day(2026, 10, 10), Day(2026, 10, 13), 300m),
            Stay(trullo, guest.Id, BookingStatus.Confirmed, BookingSource.Airbnb, Day(2026, 10, 30), Day(2026, 11, 3), 400m),
            Stay(trullo, guest.Id, BookingStatus.Cancelled, BookingSource.Direct, Day(2026, 10, 12), Day(2026, 10, 14), 250m),
            Stay(casaBianca, guest.Id, BookingStatus.CheckedIn, BookingSource.Manual, Day(2026, 10, 8), Day(2026, 10, 11), 300m),
            Stay(paused, guest.Id, BookingStatus.CheckedOut, BookingSource.BookingCom, Day(2026, 10, 5), Day(2026, 10, 8), 150m),
            Stay(foreign, foreignGuest.Id, BookingStatus.Confirmed, BookingSource.Direct, Day(2026, 10, 10), Day(2026, 10, 20), 1000m));
        if (withPreviousYear)
            db.Bookings.Add(Stay(trullo, guest.Id, BookingStatus.CheckedOut, BookingSource.Direct, Day(2025, 10, 14), Day(2025, 10, 17), 270m));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);
        return new Seeded(db, service, new HostScope(orgId), orgId, guest.Id, trullo, casaBianca, paused);
    }

    private static async Task<HostPerformanceReport> ReportAsync(
        Seeded world, HostPerformanceGroupBy groupBy, bool compare = false, IReadOnlyCollection<Guid>? only = null) =>
        await world.Service.GetPerformanceAsync(
            world.Scope, new HostPerformanceQuery(October, OctoberEnd, GroupBy: groupBy, CompareYear: compare, PropertyIds: only));

    // ---------------------------------------------------------------- proof: the total is the Home's

    [Fact]
    public async Task HostPerformance_TotalsMatchTheHome_Test()
    {
        var world = await SeedAsync(withPreviousYear: false);
        var home = await world.Service.GetKpisAsync(world.Scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October));

        // By hand: revenue 300 + 200 (the two October nights of the stay that ends in November) + 300 + 150 (the paused villa) = 950 on
        // 3 + 2 + 3 + 3 = 11 nights; 8 nights taken (5 at the Trullo, 3 at Casa Bianca) of 2 properties x 31 nights = 62.
        Assert.Equal(950m, home.Revenue);
        Assert.Equal(new HostDashboardOccupancy(8, 62, 0), home.Occupancy);

        foreach (var groupBy in Enum.GetValues<HostPerformanceGroupBy>())
        {
            var report = await ReportAsync(world, groupBy);
            var total = report.Total.Figures;

            Assert.Equal(home.Revenue, total.Revenue);
            Assert.Equal(home.RevenueStayCount, total.StayCount);
            Assert.Equal(home.Occupancy.OccupiedNights, total.OccupiedNights);
            Assert.Equal(home.Occupancy.AvailableNights, total.AvailableNights);
            Assert.Equal(home.Occupancy.ClosedNights, total.ClosedNights);
            Assert.Equal(home.Occupancy.Rate, total.OccupancyRate);
            Assert.Equal(11, total.RevenueNights);
            Assert.Equal(86.36m, total.Adr);
            Assert.Equal(15.32m, total.RevPar);
            Assert.Null(report.PreviousYearPeriod);
            Assert.Null(report.Total.PreviousYear);
        }

        // Per property: each row is what the Home gives when it is asked for that property; the paused villa keeps its money and has no
        // nights, so it has an ADR and no occupancy and no RevPAR (null, never 0).
        var byProperty = await ReportAsync(world, HostPerformanceGroupBy.Property);
        Assert.Equal(new[] { "Casa Bianca", "Trullo", "Villa in pausa" }, byProperty.Rows.Select(r => r.Label).ToArray());
        foreach (var row in byProperty.Rows.Where(r => r.PropertyId != world.Paused.Id))
        {
            var ofProperty = await world.Service.GetKpisAsync(
                world.Scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October, row.PropertyId));
            Assert.Equal(ofProperty.Revenue, row.Figures.Revenue);
            Assert.Equal(ofProperty.Occupancy.OccupiedNights, row.Figures.OccupiedNights);
            Assert.Equal(ofProperty.Occupancy.AvailableNights, row.Figures.AvailableNights);
        }

        var pausedRow = byProperty.Rows.Single(r => r.PropertyId == world.Paused.Id).Figures;
        Assert.Equal(150m, pausedRow.Revenue);
        Assert.Equal(50m, pausedRow.Adr);
        Assert.Null(pausedRow.OccupancyRate);
        Assert.Null(pausedRow.RevPar);

        // Per channel: the same stays, so the revenue adds up to the Home's; a channel has no nights, so no occupancy and no RevPAR.
        var byChannel = await ReportAsync(world, HostPerformanceGroupBy.Channel);
        Assert.Equal(new[] { "Direct", "Manual", "Airbnb", "BookingCom" }, byChannel.Rows.Select(r => r.Key).ToArray());
        Assert.Equal(home.Revenue, byChannel.Rows.Sum(r => r.Figures.Revenue));
        Assert.All(byChannel.Rows, r =>
        {
            Assert.False(r.Figures.HasNights);
            Assert.Null(r.Figures.OccupancyRate);
            Assert.Null(r.Figures.RevPar);
            Assert.NotNull(r.Figures.Adr);
        });
    }

    // ---------------------------------------------------------------- proof: the year before is the same calendar days

    [Fact]
    public async Task HostPerformance_PreviousYearUsesTheSameCalendarDays_Test()
    {
        // The rule: the same days one year earlier; 29 February becomes 28 February, so the year before is one day shorter.
        Assert.Equal((new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31)), HostReportRules.PreviousYear(October, OctoberEnd));
        Assert.Equal(
            (new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28)),
            HostReportRules.PreviousYear(new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)));
        Assert.Equal(
            (new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 5)),
            HostReportRules.PreviousYear(new DateOnly(2028, 2, 29), new DateOnly(2028, 3, 5)));
        Assert.Equal(29, HostReportRules.PeriodOf(new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)).Nights);
        Assert.Equal(28, HostReportRules.PreviousYearPeriodOf(new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)).Nights);

        // The read: the days of the report one year earlier, and for those days the numbers the Home gives.
        var world = await SeedAsync(withPreviousYear: true);
        var report = await ReportAsync(world, HostPerformanceGroupBy.None, compare: true);
        var home = await world.Service.GetKpisAsync(
            world.Scope,
            new HostDashboardQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2025, 10, 1), To: new DateOnly(2025, 10, 31)));

        Assert.Equal(Day(2025, 10, 1), report.PreviousYearPeriod!.From);
        Assert.Equal(Day(2025, 11, 1), report.PreviousYearPeriod.To);
        var before = report.Total.PreviousYear!;
        Assert.Equal(270m, before.Revenue);
        Assert.Equal(home.Revenue, before.Revenue);
        Assert.Equal(home.Occupancy.OccupiedNights, before.OccupiedNights);
        Assert.Equal(home.Occupancy.AvailableNights, before.AvailableNights);
        Assert.Equal(3, before.OccupiedNights);

        // The leap year: February and March 2028 against February and March 2027 (59 days, not 60).
        var leap = await world.Service.GetPerformanceAsync(
            world.Scope, new HostPerformanceQuery(new DateOnly(2028, 2, 1), new DateOnly(2028, 3, 31), CompareYear: true));
        Assert.Equal(60, leap.Period.Nights);
        Assert.Equal(Day(2027, 2, 1), leap.PreviousYearPeriod!.From);
        Assert.Equal(Day(2027, 4, 1), leap.PreviousYearPeriod.To);
        Assert.Equal(59, leap.PreviousYearPeriod.Nights);

        // And what the server says changed is worked out from those numbers: 950 against 270, 8 of 62 nights against 3 of 62.
        var row = PerformanceRowDto.From(await ReportRowAsync(world), 950m);
        Assert.Equal(251.9m, row.RevenueDeltaPercent);
        Assert.Equal(8.1m, row.OccupancyDeltaPoints);
        Assert.Equal(-4.0m, row.AdrDeltaPercent);
        Assert.Equal(252.2m, row.RevParDeltaPercent);
        Assert.Equal(270m, row.PreviousYear!.Revenue);
    }

    private static async Task<HostPerformanceRow> ReportRowAsync(Seeded world) =>
        (await ReportAsync(world, HostPerformanceGroupBy.None, compare: true)).Total;

    // ---------------------------------------------------------------- proof: a property the caller does not reach is a 404

    [Fact]
    public async Task HostPerformance_PropertyOutsideScope_NotFound_Test()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);

        // The collaborator «Solo alcuni» was given one property: the other one of the org and another org's are the same 404.
        foreach (var hidden in new[] { world.Hidden.Id, world.OtherOrgProperty.Id, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<NotFoundException>(() =>
                service.GetPerformanceAsync(world.Restricted, new HostPerformanceQuery(October, OctoberEnd, hidden)));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                service.GetTrendAsync(world.Restricted, new HostTrendQuery(October, OctoberEnd, hidden)));
        }

        // Another org's property is not reached by the whole org either; the one the collaborator was given is.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetPerformanceAsync(world.OrgWide, new HostPerformanceQuery(October, OctoberEnd, world.OtherOrgProperty.Id)));
        var granted = await service.GetPerformanceAsync(
            world.Restricted, new HostPerformanceQuery(October, OctoberEnd, world.Granted.Id, HostPerformanceGroupBy.Property));
        Assert.Equal(world.Granted.Id, Assert.Single(granted.Rows).PropertyId);

        // Without a property the collaborator sees only what it was given: the hidden property is not in the rows nor in the total.
        var all = await service.GetPerformanceAsync(
            world.Restricted, new HostPerformanceQuery(October, OctoberEnd, GroupBy: HostPerformanceGroupBy.Property));
        Assert.Equal(world.Granted.Id, Assert.Single(all.Rows).PropertyId);
        Assert.Equal(granted.Total.Figures.Revenue, all.Total.Figures.Revenue);
    }

    // ---------------------------------------------------------------- the other rules

    [Fact]
    public void HostPerformance_AdrAndRevPar_AreNullNeverZero_WhenTheirDenominatorIsZero()
    {
        var nothing = new HostPerformanceFigures(0, 0, 0, 0m, 0, 0);
        Assert.Null(nothing.Adr);
        Assert.Null(nothing.RevPar);
        Assert.Null(nothing.OccupancyRate);

        // Revenue without a night available (a paused property) has an ADR and no RevPAR; a channel never has the nights of a property.
        var withoutNights = new HostPerformanceFigures(0, 0, 0, 150m, 1, 3);
        Assert.Equal(50m, withoutNights.Adr);
        Assert.Null(withoutNights.RevPar);
        var channel = new HostPerformanceFigures(5, 31, 0, 100m, 1, 2) { HasNights = false };
        Assert.Equal(50m, channel.Adr);
        Assert.Null(channel.RevPar);
        Assert.Null(channel.OccupancyRate);

        // 310 over 6 nights is 51.666..., over 31 available nights 10.
        var figures = new HostPerformanceFigures(5, 31, 0, 310m, 2, 6);
        Assert.Equal(51.67m, figures.Adr);
        Assert.Equal(10m, figures.RevPar);
        Assert.Equal(5m / 31m, figures.OccupancyRate);
    }

    [Fact]
    public void HostPerformance_Deltas_NeverDivideByZero_AndAreTheHomesForTheOccupancy()
    {
        Assert.Equal(10.0m, HostPerformanceRules.DeltaPercent(110m, 100m));
        Assert.Equal(-50.0m, HostPerformanceRules.DeltaPercent(50m, 100m));
        Assert.Null(HostPerformanceRules.DeltaPercent(100m, 0m));
        Assert.Null(HostPerformanceRules.DeltaPercent(100m, null));
        Assert.Null(HostPerformanceRules.DeltaPercent(null, 100m));

        Assert.Equal(10.0m, HostPerformanceRules.DeltaPoints(0.5m, 0.4m));
        Assert.Equal(40.0m, HostPerformanceRules.DeltaPoints(0.4m, 0m));
        Assert.Null(HostPerformanceRules.DeltaPoints(null, 0.4m));
        Assert.Null(HostPerformanceRules.DeltaPoints(0.4m, null));
        Assert.Equal(
            HostOverviewRules.OccupancyDeltaPoints(new HostDashboardOccupancy(5, 10, 0), new HostDashboardOccupancy(3, 10, 0)),
            HostPerformanceRules.DeltaPoints(0.5m, 0.3m));
    }

    [Fact]
    public async Task HostPerformance_PropertyThatDidNotExistAYearAgo_HasNoPreviousYear_NeverZeros()
    {
        var world = await SeedAsync(withPreviousYear: true);

        // A house created in June 2026 with a stay in October: a year ago it was not there.
        var newcomer = NewHouse(world.OrgId, "Casa Nuova", Day(2026, 6, 1));
        world.Db.Properties.Add(newcomer);
        world.Db.Bookings.Add(Stay(newcomer, world.GuestId, BookingStatus.Confirmed, BookingSource.Direct, Day(2026, 10, 20), Day(2026, 10, 22), 200m));
        await world.Db.SaveChangesAsync();
        world.Db.ChangeTracker.Clear();

        var report = await ReportAsync(world, HostPerformanceGroupBy.Property, compare: true);

        Assert.Null(report.Rows.Single(r => r.PropertyId == newcomer.Id).PreviousYear);
        Assert.Equal(200m, report.Rows.Single(r => r.PropertyId == newcomer.Id).Figures.Revenue);
        // A house that was there has its year before, with the stay of 2025; Casa Bianca has none, but it existed and had its 31 nights.
        Assert.Equal(270m, report.Rows.Single(r => r.PropertyId == world.Trullo.Id).PreviousYear!.Revenue);
        var quiet = report.Rows.Single(r => r.PropertyId == world.CasaBianca.Id).PreviousYear!;
        Assert.Equal(0, quiet.StayCount);
        Assert.Equal(31, quiet.AvailableNights);
        Assert.NotNull(report.Total.PreviousYear);

        // A channel with no stay a year ago has nothing to compare with.
        var channels = await ReportAsync(world, HostPerformanceGroupBy.Channel, compare: true);
        Assert.Null(channels.Rows.Single(r => r.Key == nameof(BookingSource.Airbnb)).PreviousYear);
        Assert.Equal(270m, channels.Rows.Single(r => r.Key == nameof(BookingSource.Direct)).PreviousYear!.Revenue);
    }

    [Fact]
    public async Task HostPerformance_PropertyIds_NarrowInsideTheScope_NeverWiden()
    {
        var world = await SeedAsync(withPreviousYear: false);
        var home = await world.Service.GetKpisAsync(
            world.Scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October, world.Trullo.Id));

        // Only the Trullo: what the Home gives for it. Another org's property in the set adds nothing.
        var narrowed = await ReportAsync(world, HostPerformanceGroupBy.Property, only: [world.Trullo.Id, Guid.NewGuid()]);
        Assert.Equal(world.Trullo.Id, Assert.Single(narrowed.Rows).PropertyId);
        Assert.Equal(home.Revenue, narrowed.Total.Figures.Revenue);
        Assert.Equal(home.Occupancy.AvailableNights, narrowed.Total.Figures.AvailableNights);

        // An empty set is nothing, not everything.
        var empty = await ReportAsync(world, HostPerformanceGroupBy.Property, only: []);
        Assert.Empty(empty.Rows);
        Assert.Equal(0m, empty.Total.Figures.Revenue);
        Assert.Equal(0, empty.Total.Figures.AvailableNights);

        // A property outside the set is a 404 even when the scope reaches it.
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.GetPerformanceAsync(
            world.Scope, new HostPerformanceQuery(October, OctoberEnd, world.CasaBianca.Id, PropertyIds: [world.Trullo.Id])));
    }

    // ---------------------------------------------------------------- the trend

    [Fact]
    public async Task HostTrend_BucketsAddUpToThePeriod_AndTheYearBeforeIsCoveredOnce()
    {
        var world = await SeedAsync(withPreviousYear: true);
        var trend = await world.Service.GetTrendAsync(world.Scope, new HostTrendQuery(October, OctoberEnd, CompareYear: true));

        // The weeks of the Home's chart, cut at the edges: Thursday 1 to Sunday 4, three whole weeks, Monday 26 to Saturday 31.
        Assert.Equal(HostOverviewSeriesUnit.Week, trend.Unit);
        Assert.Equal(5, trend.Buckets.Count);
        Assert.Equal(trend.Period.From, trend.Buckets[0].From);
        Assert.Equal(trend.Period.To, trend.Buckets[^1].To);
        for (var index = 1; index < trend.Buckets.Count; index++)
        {
            Assert.Equal(trend.Buckets[index - 1].To, trend.Buckets[index].From);
            Assert.Equal(trend.Buckets[index - 1].PreviousYearTo, trend.Buckets[index].PreviousYearFrom);
        }

        // The year before: its period, covered exactly by its buckets.
        Assert.Equal(trend.PreviousYearPeriod!.From, trend.Buckets[0].PreviousYearFrom);
        Assert.Equal(trend.PreviousYearPeriod.To, trend.Buckets[^1].PreviousYearTo);

        // One line for the whole scope, and the cuts add up to the period: same stays, same nights.
        var total = Assert.Single(trend.Series);
        Assert.Equal(HostPerformanceRules.TotalKey, total.Key);
        Assert.Equal(new[] { 0m, 650m, 100m, 0m, 200m }, total.Points.Select(p => p.Figures.Revenue).ToArray());
        Assert.Equal(950m, total.Points.Sum(p => p.Figures.Revenue));
        Assert.Equal(8, total.Points.Sum(p => p.Figures.OccupiedNights));
        Assert.Equal(62, total.Points.Sum(p => p.Figures.AvailableNights));
        Assert.Equal(5, total.PreviousYear.Count);
        Assert.Equal(new[] { 0m, 0m, 270m, 0m, 0m }, total.PreviousYear.Select(p => p!.Figures.Revenue).ToArray());
        Assert.Equal(3, total.PreviousYear.Sum(p => p!.Figures.OccupiedNights));
        Assert.Equal(62, total.PreviousYear.Sum(p => p!.Figures.AvailableNights));

        // One line for each property, the one with most revenue first, and each line adds up to the row of the report.
        var byProperty = await world.Service.GetTrendAsync(
            world.Scope, new HostTrendQuery(October, OctoberEnd, GroupBy: HostPerformanceGroupBy.Property));
        var report = await ReportAsync(world, HostPerformanceGroupBy.Property);
        Assert.Equal(new[] { "Trullo", "Casa Bianca", "Villa in pausa" }, byProperty.Series.Select(s => s.Label).ToArray());
        Assert.All(byProperty.Series, line =>
            Assert.Equal(report.Rows.Single(r => r.PropertyId == line.PropertyId).Figures.Revenue, line.Points.Sum(p => p.Figures.Revenue)));
        Assert.Empty(byProperty.Series[0].PreviousYear);

        // Months when asked, and by the rule of the period when not: weeks up to 92 days, months beyond.
        var monthly = await world.Service.GetTrendAsync(
            world.Scope, new HostTrendQuery(October, OctoberEnd, Bucket: HostTrendBucketUnit.Month));
        Assert.Equal("2026-10", Assert.Single(monthly.Buckets).Label);
        var year = await world.Service.GetTrendAsync(
            world.Scope, new HostTrendQuery(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(12, year.Buckets.Count);
        Assert.Equal(HostOverviewSeriesUnit.Week, HostReportRules.TrendUnitFor(HostReportRules.PeriodOf(October, October.AddDays(91)), HostTrendBucketUnit.Auto));
        Assert.Equal(HostOverviewSeriesUnit.Month, HostReportRules.TrendUnitFor(HostReportRules.PeriodOf(October, October.AddDays(92)), HostTrendBucketUnit.Auto));
    }

    [Fact]
    public void HostTrend_PreviousYearBuckets_CoverTheYearBeforeOnce_EvenWithALeapDay()
    {
        // Three buckets of February and March 2028: the 22nd to the 28th, the 29th alone, the 1st to the 5th of March.
        var buckets = new List<(DateTime From, DateTime To)>
        {
            (Day(2028, 2, 22), Day(2028, 2, 29)),
            (Day(2028, 2, 29), Day(2028, 3, 1)),
            (Day(2028, 3, 1), Day(2028, 3, 6)),
        };

        var moved = HostReportRules.PreviousYearBuckets(buckets);

        // 28 February 2027 answers to both the 28th and the 29th: it stays with the 28th, and the 29th has no day of its own.
        Assert.Equal((Day(2027, 2, 22), Day(2027, 3, 1)), moved[0]);
        Assert.Equal((Day(2027, 3, 1), Day(2027, 3, 1)), moved[1]);
        Assert.Equal((Day(2027, 3, 1), Day(2027, 3, 6)), moved[2]);
        var (first, last) = HostReportRules.PreviousYear(new DateOnly(2028, 2, 22), new DateOnly(2028, 3, 5));
        Assert.Equal(first.ToDateTime(TimeOnly.MinValue), moved[0].From);
        Assert.Equal(last.AddDays(1).ToDateTime(TimeOnly.MinValue), moved[^1].To);
    }

    [Fact]
    public async Task HostTrend_MoreThan25Lines_KeepThe24WithMostRevenue_AndSumTheRestInOther()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Guests.Add(guest);
        for (var number = 1; number <= 27; number++)
        {
            var house = NewHouse(orgId, $"Casa {number:00}");
            db.Properties.Add(house);
            db.Bookings.Add(Stay(house, guest.Id, BookingStatus.Confirmed, BookingSource.Direct, Day(2026, 10, 10), Day(2026, 10, 11), number * 100m));
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);

        var trend = await service.GetTrendAsync(
            new HostScope(orgId), new HostTrendQuery(October, OctoberEnd, GroupBy: HostPerformanceGroupBy.Property));

        // 27 houses: the 24 with most revenue (Casa 27 down to Casa 04) and one line for the three left (Casa 03, 02 and 01: 300 + 200 + 100).
        Assert.Equal(HostPerformanceRules.MaxTrendSeries, trend.Series.Count);
        Assert.Equal("Casa 27", trend.Series[0].Label);
        Assert.Equal("Casa 04", trend.Series[^2].Label);
        var other = trend.Series[^1];
        Assert.Equal(HostPerformanceRules.OtherSeriesKey, other.Key);
        Assert.Null(other.PropertyId);
        Assert.Equal(600m, other.Points.Sum(p => p.Figures.Revenue));
        Assert.Equal(3, other.Points.Sum(p => p.Figures.OccupiedNights));
        // Nothing is dropped: all the lines together are the revenue of the whole scope.
        Assert.Equal(37800m, trend.Series.Sum(s => s.Points.Sum(p => p.Figures.Revenue)));
    }

    // ---------------------------------------------------------------- the files

    [Fact]
    public async Task HostReportDocuments_PerformanceCsv_CarriesTheTitleWithItsBase_AndTheYearBeforeOnlyWhenCompared()
    {
        var world = await SeedAsync(withPreviousYear: true);

        static string[] Lines(byte[] csv) =>
            Encoding.UTF8.GetString(csv).TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var plain = Lines(HostReportDocuments.Csv(await ReportAsync(world, HostPerformanceGroupBy.Property), HostPerformanceGroupBy.Property));
        Assert.Equal("Prestazioni per immobile, per competenza, dal 01/10/2026 al 31/10/2026", plain[0]);
        Assert.StartsWith("Immobile;Notti occupate;", plain[1]);
        Assert.DoesNotContain("anno precedente", plain[1], StringComparison.Ordinal);
        Assert.Equal(2 + 3 + 1, plain.Length);
        Assert.StartsWith("Totale;8;62;0;", plain[^1]);

        var compared = Lines(
            HostReportDocuments.Csv(await ReportAsync(world, HostPerformanceGroupBy.Property, compare: true), HostPerformanceGroupBy.Property));
        Assert.Equal(
            "Prestazioni per immobile, per competenza, dal 01/10/2026 al 31/10/2026, confrontato con dal 01/10/2025 al 31/10/2025",
            compared[0]);
        Assert.Contains("Ricavo anno precedente (€)", compared[1], StringComparison.Ordinal);
        Assert.Contains("Variazione ricavo (%)", compared[1], StringComparison.Ordinal);

        // A channel has no nights: the cells are empty, and the title says channels.
        var channels = Lines(HostReportDocuments.Csv(await ReportAsync(world, HostPerformanceGroupBy.Channel), HostPerformanceGroupBy.Channel));
        Assert.StartsWith("Prestazioni per canale, per competenza, dal 01/10/2026 al 31/10/2026", channels[0]);
        Assert.StartsWith("Canale;", channels[1]);

        // The trend: the title, a row for each bucket of the line, the year before only when compared.
        var trend = Lines(HostReportDocuments.Csv(
            await world.Service.GetTrendAsync(world.Scope, new HostTrendQuery(October, OctoberEnd)), HostPerformanceGroupBy.None));
        Assert.Equal("Andamento, per competenza, dal 01/10/2026 al 31/10/2026", trend[0]);
        Assert.Equal(2 + 5, trend.Length);
        Assert.DoesNotContain("anno precedente", trend[1], StringComparison.Ordinal);
    }
}
