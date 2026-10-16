using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03b on the numbers of the Home, with the figures worked out by hand: the period and the one before it, the differences the
/// server works out, the stays that arrive (a request waiting for the host among them), the next arrivals, the chart that adds up
/// to the revenue, and the money that a caller who may not read payments never gets. Today is 9 October 2026 in Rome.
/// </summary>
public class HostOverviewServiceTests
{
    private const string Collaborator = "auth0|collaboratore-sr03b";

    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static HostDashboardService Dashboard(AppDbContext db, TimeProvider? clock = null) =>
        new(db, new ConfigurationBuilder().Build(), clock ?? Clock);

    private sealed record Fixture(
        AppDbContext Db,
        Guid OrgId,
        Property Trullo,
        Property CasaBianca,
        Property LongRent,
        Property Foreign,
        Booking A,
        Booking Request,
        Booking C,
        HostScope OrgWide,
        HostScope Restricted);

    /// <summary>
    /// Two short-rent properties and one in long-term mode, and the stays and the payments the tests count; the collaborator
    /// reaches the Trullo only.
    ///
    ///   A  Trullo       Direct   Confirmed    10-13 Oct, 300  (nights of the 10th, 11th, 12th)
    ///   B  Casa Bianca  Manual   CheckedIn     8-11 Oct, 300  (the 8th, 9th, 10th: it arrived the 8th)
    ///   C  Trullo       Airbnb   Confirmed     6-10 Nov, 400
    ///   D  Trullo       Direct   Cancelled    12-14 Oct, 250  (counts for nothing)
    ///   F  Trullo       Direct   CheckedOut   20-25 Sep, 500  (the period before October)
    ///   R  Trullo       Direct   Pending      20-23 Oct, 600  (a "pay at the property" request waiting for the host)
    ///   W  Trullo       Direct   Pending      24-26 Oct, 600  (a request whose guest has not confirmed the email: nobody's yet)
    ///   X  Foreign      Direct   Confirmed    10-20 Oct, 1000 (another org)
    /// </summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Casa Bianca");
        var longRent = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Affitto lungo");
        longRent.RentalMode = RentalMode.Long;
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-sr03b", "Villa Altrove");
        db.Properties.AddRange(trullo, casaBianca, longRent, foreign);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = Collaborator, PropertyId = trullo.Id });

        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var foreignGuest = new Guest { OrgId = otherOrgId, FirstName = "Luca", LastName = "Neri", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Guests.AddRange(guest, foreignGuest);

        var a = Stay(trullo, guest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 13), 300m);
        var b = Stay(casaBianca, guest, BookingStatus.CheckedIn, BookingSource.Manual, Day(10, 8), Day(10, 11), 300m);
        var c = Stay(trullo, guest, BookingStatus.Confirmed, BookingSource.Airbnb, Day(11, 6), Day(11, 10), 400m);
        var d = Stay(trullo, guest, BookingStatus.Cancelled, BookingSource.Direct, Day(10, 12), Day(10, 14), 250m);
        var f = Stay(trullo, guest, BookingStatus.CheckedOut, BookingSource.Direct, Day(9, 20), Day(9, 25), 500m);
        var request = Stay(trullo, guest, BookingStatus.Pending, BookingSource.Direct, Day(10, 20), Day(10, 23), 600m);
        request.PaymentOption = PaymentOption.OnSite;
        request.GuestEmailVerifiedAt = At(10, 9, 8);
        request.RequestExpiresAt = At(10, 10, 8);
        var waiting = Stay(trullo, guest, BookingStatus.Pending, BookingSource.Direct, Day(10, 24), Day(10, 26), 600m);
        waiting.PaymentOption = PaymentOption.OnSite;
        waiting.RequestExpiresAt = At(10, 9, 12);
        var elsewhere = Stay(foreign, foreignGuest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 20), 1000m);
        db.Bookings.AddRange(a, b, c, d, f, request, waiting, elsewhere);

        db.Payments.AddRange(
            Pay(a, PaymentStatus.Completed, 300m, At(10, 5, 10)),
            Pay(b, PaymentStatus.PartiallyRefunded, 200m, At(10, 6, 10), refunded: 50m),
            Pay(f, PaymentStatus.Completed, 500m, At(9, 30, 10)),
            Pay(elsewhere, PaymentStatus.Completed, 999m, At(10, 5, 10)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Fixture(
            db, orgId, trullo, casaBianca, longRent, foreign, a, request, c, new HostScope(orgId), new HostScope(orgId, GrantedToUserId: Collaborator));
    }

    private static Booking Stay(
        Property property, Guest guest, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal basePrice)
    {
        return new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            Source = source,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            BasePrice = basePrice,
            TotalPrice = basePrice,
        };
    }

    private static Payment Pay(Booking booking, PaymentStatus status, decimal amount, DateTime processed, decimal refunded = 0m) => new()
    {
        OrgId = booking.OrgId,
        BookingId = booking.Id,
        Status = status,
        Amount = amount,
        RefundedAmount = refunded,
        ProcessedAt = processed,
        CreatedAt = processed,
        UpdatedAt = processed,
    };

    private static HostOverviewQuery October(bool money = true, Guid? propertyId = null) =>
        new(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), PropertyId: propertyId, IncludeMoney: money);

    // --- The figures, compared with the period before --------------------------------------------------------

    [Fact]
    public async Task October_TheFiguresOfTheKpis_AndTheOnesOfTheThirtyOneNightsBefore()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October());

        Assert.Equal((HostDashboardPeriodKind.Month, Day(10, 1), Day(11, 1), 31), (overview.Period.Kind, overview.Period.From, overview.Period.To, overview.Period.Nights));
        Assert.Equal(Day(10, 9), overview.TodayInRome);
        // Two short-rent properties; the one in long-term mode is out. A 3 nights + B 3 + the two requests that hold their dates
        // (R 3, W 2) of 62.
        Assert.Equal(2, overview.PropertyCount);
        Assert.Equal(new HostDashboardOccupancy(11, 62, 0), overview.Occupancy.Occupancy);
        // The request is not revenue: A 300 + B 300 (the pending ones and the cancelled stay count for nothing).
        Assert.Equal(new HostOverviewRevenue(600m, 2, 20.0m), overview.Revenue);
        Assert.Equal(new HostDashboardDirectShare(1, 2), overview.DirectShare);

        // The period before: 31 nights from 31 August; F (5 nights) on the Trullo.
        Assert.Equal((Day(8, 31), Day(10, 1), 31), (overview.Previous.Period.From, overview.Previous.Period.To, overview.Previous.Period.Nights));
        Assert.Equal(new HostDashboardOccupancy(5, 62, 0), overview.Previous.Occupancy.Occupancy);
        Assert.Equal(new HostOverviewRevenue(500m, 1, null), overview.Previous.Revenue);
        Assert.Equal(new HostDashboardDirectShare(1, 1), overview.Previous.DirectShare);
        // 11/62 against 5/62 of the nights: 9.7 points more; 600 against 500: 20 % more. Worked out here, not by the client.
        Assert.Equal(9.7m, overview.Occupancy.DeltaPoints);
        Assert.Null(overview.Previous.Occupancy.DeltaPoints);
    }

    [Fact]
    public async Task TheFiguresAreThoseOfTheKpis_NotAnotherCopyOfTheRules()
    {
        var world = await SeedAsync();
        var dashboard = Dashboard(world.Db);

        var overview = await dashboard.GetOverviewAsync(world.OrgWide, October());
        var kpis = await dashboard.GetKpisAsync(
            world.OrgWide,
            new HostDashboardQuery(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), Compare: true, IncludeCollected: true));

        Assert.Equal(kpis.PropertyCount, overview.PropertyCount);
        Assert.Equal(kpis.Occupancy, overview.Occupancy.Occupancy);
        Assert.Equal(kpis.Revenue, overview.Revenue!.Amount);
        Assert.Equal(kpis.RevenueStayCount, overview.Revenue.StayCount);
        Assert.Equal(kpis.Collected, overview.Collected);
        Assert.Equal(kpis.DirectShare, overview.DirectShare);
        Assert.Equal(kpis.Previous!.Occupancy, overview.Previous.Occupancy.Occupancy);
        Assert.Equal(kpis.Previous.Revenue, overview.Previous.Revenue!.Amount);
        Assert.Equal(kpis.Previous.Collected, overview.Previous.Collected);
        Assert.Equal(kpis.Previous.Period, overview.Previous.Period);
    }

    [Fact]
    public async Task TheMoneyCollected_IsByCash_AndIsThereForWhoMayReadMoney()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October());

        // A 300 and B 200 - 50 refunded came in during October; F's 500 came in on 30 September.
        Assert.Equal(new HostDashboardCollected(450m, 2), overview.Collected);
        Assert.Equal(new HostDashboardCollected(500m, 1), overview.Previous.Collected);
    }

    [Fact]
    public async Task WithoutMoney_NoRevenueNoCashNoSeries_NotEvenInThePeriodBefore_ButEverythingElse()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October(money: false));

        Assert.Null(overview.Revenue);
        Assert.Null(overview.Collected);
        Assert.Null(overview.Series);
        Assert.Null(overview.Previous.Revenue);
        Assert.Null(overview.Previous.Collected);
        // What is not money is there: the occupancy and its difference, the stays that arrive, the share of the booking site.
        Assert.Equal(new HostDashboardOccupancy(11, 62, 0), overview.Occupancy.Occupancy);
        Assert.Equal(9.7m, overview.Occupancy.DeltaPoints);
        Assert.Equal(3, overview.Upcoming.Count);
        Assert.Equal(new HostDashboardDirectShare(1, 2), overview.DirectShare);
        Assert.Equal(3, overview.NextArrivals.Count);
    }

    // --- The stays that arrive -----------------------------------------------------------------------------

    [Fact]
    public async Task Upcoming_TheStaysThatArriveInThePeriod_TheRequestsWaitingForTheHostAmongThem()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October());

        // A (the 10th), B (the 8th: it arrived already, it still arrived in October) and the request (the 20th); not the cancelled
        // stay, not the request whose guest has not confirmed the email, not C (November).
        Assert.Equal(new HostOverviewUpcoming(Count: 3, PreviousCount: 1, PendingApproval: 1), overview.Upcoming);
    }

    [Fact]
    public async Task Upcoming_ARequestPastItsTerm_IsNotCounted()
    {
        var world = await SeedAsync();
        // A day later the request of the 20th expired by itself (its term was the 10th at 08:00Z).
        var later = new Casazen.Tests.Unit.FixedTimeProvider(new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero));

        var overview = await Dashboard(world.Db, later).GetOverviewAsync(world.OrgWide, October());

        Assert.Equal(new HostOverviewUpcoming(Count: 2, PreviousCount: 1, PendingApproval: 0), overview.Upcoming);
    }

    [Fact]
    public async Task NextArrivals_ThreeFromToday_WhateverThePeriod_ARequestAmongThem_SoonestFirst()
    {
        var world = await SeedAsync();

        // The period asked for is October of last year: the next arrivals do not depend on it.
        var overview = await Dashboard(world.Db).GetOverviewAsync(
            world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2025, 10, 1), To: new DateOnly(2025, 10, 31)));

        Assert.Equal(
            [("Confirmed", Day(10, 10), false), ("Pending", Day(10, 20), true), ("Confirmed", Day(11, 6), false)],
            overview.NextArrivals.Select(s => (s.Status.ToString(), s.CheckInDate, s.Status == BookingStatus.Pending)));
        Assert.Equal([world.Trullo.Id, world.Trullo.Id, world.Trullo.Id], overview.NextArrivals.Select(s => s.PropertyId));
        Assert.Equal("Anna Verdi", overview.NextArrivals[0].GuestName);
        Assert.False(string.IsNullOrEmpty(overview.NextArrivals[0].BookingCode));
    }

    [Fact]
    public async Task NextArrivals_NeverAStayThatArrivedAlready_ACancelledOneOrAnotherOrgs()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October());

        // B arrived on the 8th, D is cancelled, X is another org's (10 to 20 October, a Confirmed stay).
        Assert.DoesNotContain(overview.NextArrivals, s => s.PropertyName is "Casa Bianca" or "Villa Altrove");
        Assert.Equal([world.A.Id], overview.NextArrivals.Take(1).Select(s => s.BookingId));
    }

    // --- The chart --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Series_ByWeek_AddsUpToTheRevenue_AndToThePreviousRevenue()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October());

        var series = overview.Series!;
        Assert.Equal(HostOverviewSeriesUnit.Week, series.Unit);
        Assert.Equal(
            [("2026-W40", Day(10, 1), Day(10, 5)), ("2026-W41", Day(10, 5), Day(10, 12)), ("2026-W42", Day(10, 12), Day(10, 19)), ("2026-W43", Day(10, 19), Day(10, 26)), ("2026-W44", Day(10, 26), Day(11, 1))],
            series.Buckets.Select(b => (b.Label, b.From, b.To)));
        // B's three nights and A's first two in the week from the 5th; A's third night in the next. The request is not revenue.
        Assert.Equal([0m, 500m, 100m, 0m, 0m], series.Buckets.Select(b => b.Amount));
        Assert.Equal(overview.Revenue!.Amount, series.Buckets.Sum(b => b.Amount));
        // F's five nights of September (the 20th to the 24th) fall in the bar of the 19th to the 25th, 31 days earlier.
        Assert.Equal([0m, 0m, 0m, 500m, 0m], series.Buckets.Select(b => b.PreviousAmount));
        Assert.Equal(overview.Previous.Revenue!.Amount, series.Buckets.Sum(b => b.PreviousAmount));
        // Today is the 9th: the bars from the 12th are forecasts of stays already confirmed.
        Assert.Equal([false, false, true, true, true], series.Buckets.Select(b => b.IsFuture));
    }

    [Fact]
    public async Task Series_AYearByMonth_AddsUpToo()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(
            world.OrgWide,
            new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31), IncludeMoney: true));

        var series = overview.Series!;
        Assert.Equal(HostOverviewSeriesUnit.Month, series.Unit);
        Assert.Equal(12, series.Buckets.Count);
        // F in September (500), A and B in October (600), C in November (400): 1,500 over the year.
        Assert.Equal(1500m, overview.Revenue!.Amount);
        Assert.Equal(overview.Revenue.Amount, series.Buckets.Sum(b => b.Amount));
        Assert.Equal(500m, series.Buckets.Single(b => b.Label == "2026-09").Amount);
        Assert.Equal(600m, series.Buckets.Single(b => b.Label == "2026-10").Amount);
        Assert.Equal(400m, series.Buckets.Single(b => b.Label == "2026-11").Amount);
        Assert.Equal(["2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11", "2026-12"], series.Buckets.Select(b => b.Label));
        Assert.Equal(365, overview.Period.Nights);
    }

    [Fact]
    public async Task Series_TheCentsAddUp_WhateverTheRounding()
    {
        var world = await SeedAsync();
        // Three nights of 100 euros on the Trullo from Sunday the 4th: Sunday alone in the first week, Monday and Tuesday in the next.
        world.Db.Bookings.Add(Stay(world.Trullo, await world.Db.Guests.FirstAsync(g => g.OrgId == world.OrgId), BookingStatus.Confirmed, BookingSource.Direct, Day(10, 4), Day(10, 7), 100m));
        await world.Db.SaveChangesAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, October());

        Assert.Equal(700m, overview.Revenue!.Amount);
        Assert.Equal(overview.Revenue.Amount, overview.Series!.Buckets.Sum(b => b.Amount));
        // 33.33 on the 4th (W40), 66.67 on the 5th and 6th (W41) = 100: no cent lost to the rounding.
        Assert.Equal(33.33m, overview.Series.Buckets[0].Amount);
        Assert.Equal(566.67m, overview.Series.Buckets[1].Amount);
    }

    // --- A free period ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Custom_ThePeriodIsTheDaysAsked_AndTheOneBeforeHasTheSameNights()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(
            world.OrgWide,
            new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 10, 5), To: new DateOnly(2026, 10, 11), IncludeMoney: true));

        Assert.Equal((HostDashboardPeriodKind.Custom, Day(10, 5), Day(10, 12), 7), (overview.Period.Kind, overview.Period.From, overview.Period.To, overview.Period.Nights));
        Assert.Equal((Day(9, 28), Day(10, 5), 7), (overview.Previous.Period.From, overview.Previous.Period.To, overview.Previous.Period.Nights));
        // B all three nights, A's first two: 300 + 200. A's third night is the 12th.
        Assert.Equal(500m, overview.Revenue!.Amount);
        var week = Assert.Single(overview.Series!.Buckets);
        Assert.Equal(500m, week.Amount);
        // 7 nights x 2 properties.
        Assert.Equal(new HostDashboardOccupancy(5, 14, 0), overview.Occupancy.Occupancy);
    }

    [Fact]
    public async Task Custom_WithoutBothDays_OrOutOfOrder_OrTooLong_IsRefusedByTheService_TheWebLayerChecksFirst()
    {
        var world = await SeedAsync();
        var service = Dashboard(world.Db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetOverviewAsync(
            world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Custom)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetOverviewAsync(
            world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 10, 20), To: new DateOnly(2026, 10, 10))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetOverviewAsync(
            world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 1, 1), To: new DateOnly(2027, 1, 2))));
    }

    [Fact]
    public async Task Next30Days_AndLast30Days_AreThereToo()
    {
        var world = await SeedAsync();

        var next = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, IncludeMoney: true));
        var last = await Dashboard(world.Db).GetOverviewAsync(world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Last30Days, IncludeMoney: true));

        Assert.Equal((Day(10, 9), Day(11, 8)), (next.Period.From, next.Period.To));
        Assert.Equal((Day(9, 10), Day(10, 10)), (last.Period.From, last.Period.To));
        Assert.Equal(next.Revenue!.Amount, next.Series!.Buckets.Sum(b => b.Amount));
        Assert.Equal(last.Revenue!.Amount, last.Series!.Buckets.Sum(b => b.Amount));
        // Every bar of the last 30 days is in the past or holds today: none is a forecast.
        Assert.DoesNotContain(last.Series.Buckets, b => b.IsFuture);
    }

    // --- One property, and the scope -------------------------------------------------------------------------

    [Fact]
    public async Task PropertyFilter_NarrowsEveryNumberToTheProperty_AndNotFoundWhenItIsNotTheCallers()
    {
        var world = await SeedAsync();
        var service = Dashboard(world.Db);

        var overview = await service.GetOverviewAsync(world.OrgWide, October(propertyId: world.Trullo.Id));

        Assert.Equal(1, overview.PropertyCount);
        // A 3 nights, the request 3 and the other 2 that hold their dates, of 31.
        Assert.Equal(new HostDashboardOccupancy(8, 31, 0), overview.Occupancy.Occupancy);
        Assert.Equal(300m, overview.Revenue!.Amount);
        Assert.Equal(2, overview.Upcoming.Count);
        Assert.All(overview.NextArrivals, s => Assert.Equal(world.Trullo.Id, s.PropertyId));

        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => service.GetOverviewAsync(world.OrgWide, October(propertyId: world.Foreign.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetOverviewAsync(world.OrgWide, October(propertyId: Guid.NewGuid())));
        var notGiven = await Assert.ThrowsAsync<NotFoundException>(() => service.GetOverviewAsync(world.Restricted, October(propertyId: world.CasaBianca.Id)));
        Assert.Equal("PropertyNotFound", foreign.MessageKey);
        Assert.Equal("PropertyNotFound", notGiven.MessageKey);
    }

    [Fact]
    public async Task Restricted_TheCollaboratorSeesTheNumbersOfItsPropertyOnly_Everywhere()
    {
        var world = await SeedAsync();

        var overview = await Dashboard(world.Db).GetOverviewAsync(world.Restricted, October());

        Assert.Equal(1, overview.PropertyCount);
        Assert.Equal(new HostDashboardOccupancy(8, 31, 0), overview.Occupancy.Occupancy);
        Assert.Equal(300m, overview.Revenue!.Amount);
        // A and the request; B (Casa Bianca) is not the collaborator's.
        Assert.Equal(2, overview.Upcoming.Count);
        Assert.All(overview.NextArrivals, s => Assert.Equal(world.Trullo.Id, s.PropertyId));
        Assert.Equal(overview.Revenue.Amount, overview.Series!.Buckets.Sum(b => b.Amount));
        // The period before: F on the Trullo.
        Assert.Equal(500m, overview.Previous.Revenue!.Amount);
        Assert.Equal(new HostDashboardCollected(300m, 1), overview.Collected);
    }

    [Fact]
    public async Task ACallerWithNoPropertiesAtAll_GetsZeroes_NotAnError()
    {
        var db = HostScopeScenario.NewInMemoryDb();

        var overview = await Dashboard(db).GetOverviewAsync(new HostScope(Guid.NewGuid()), October());

        Assert.Equal(0, overview.PropertyCount);
        Assert.Equal(new HostDashboardOccupancy(0, 0, 0), overview.Occupancy.Occupancy);
        Assert.Null(overview.Occupancy.DeltaPoints);
        Assert.Equal(new HostOverviewRevenue(0m, 0, null), overview.Revenue);
        Assert.Equal(new HostOverviewUpcoming(0, 0, 0), overview.Upcoming);
        Assert.Empty(overview.NextArrivals);
        Assert.All(overview.Series!.Buckets, b => Assert.Equal(0m, b.Amount));
    }
}
