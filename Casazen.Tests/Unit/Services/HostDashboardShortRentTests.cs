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
/// SR-03 on the host dashboard, with the numbers worked out by hand: the next 30 days, the period before (same number of days),
/// the money collected by cash, the share of the stays that came from the booking site, the properties in long-term mode left
/// out, the one-property filter and the reach of a collaborator limited to some properties. Today is 9 October 2026 in Rome.
/// </summary>
public class HostDashboardShortRentTests
{
    private const string Collaborator = "auth0|collaboratore-sr03";

    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static HostDashboardService Dashboard(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private sealed record Fixture(
        AppDbContext Db,
        Guid OrgId,
        Property Trullo,
        Property CasaBianca,
        Property LongRent,
        Property Foreign,
        HostScope OrgWide,
        HostScope Restricted);

    // --- The data ---------------------------------------------------------------------------------------

    /// <summary>
    /// Two short-rent properties and one in long-term mode, the stays and the payments the tests count, and the stays and the
    /// payment of another org. The collaborator reaches the Trullo only.
    ///
    ///   A  Trullo       Direct   Confirmed   10-13 Oct, 300   (3 nights, all in the next 30 days)
    ///   B  Casa Bianca  Manual   CheckedIn    8-11 Oct, 300   (night of the 8th in the period before, 9th and 10th in the next)
    ///   C  Trullo       Airbnb   Confirmed    6-10 Nov, 400   (nights of the 6th and 7th in the next 30 days, 2 of 4)
    ///   D  Trullo       Direct   Cancelled   12-14 Oct, 250   (counts for nothing)
    ///   F  Trullo       Direct   CheckedOut  20-25 Sep, 500   (the period before)
    /// </summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Casa Bianca");
        var longRent = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Affitto lungo");
        longRent.RentalMode = RentalMode.Long;
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-sr03", "Villa Altrove");
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
        var elsewhere = Stay(foreign, foreignGuest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 20), 1000m);
        db.Bookings.AddRange(a, b, c, d, f, elsewhere);

        // The payments: four in the next 30 days (the last of them has no instant of settlement, recorded before it existed: the day
        // it was created counts), three in the period before (the 23:59 of the 8th in Rome among them: 21:59Z), and the rest
        // that do not count (a refunded one, a failed one, another org's).
        db.Payments.AddRange(
            Pay(a, PaymentStatus.Completed, 300m, processed: At(10, 12, 10)),
            Pay(b, PaymentStatus.PartiallyRefunded, 200m, refunded: 50m, processed: At(10, 20, 10)),
            Pay(a, PaymentStatus.Refunded, 300m, refunded: 300m, processed: At(10, 15, 10)),
            Pay(a, PaymentStatus.Failed, 300m, created: At(10, 14, 10)),
            Pay(c, PaymentStatus.Completed, 100m, created: At(10, 25, 10)),
            Pay(f, PaymentStatus.Completed, 500m, processed: At(9, 30, 10)),
            Pay(b, PaymentStatus.Completed, 10m, processed: At(10, 8, 22, 30)),
            Pay(b, PaymentStatus.Completed, 20m, processed: At(10, 8, 21, 59)),
            Pay(elsewhere, PaymentStatus.Completed, 999m, processed: At(10, 12, 10)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Fixture(
            db, orgId, trullo, casaBianca, longRent, foreign, new HostScope(orgId), new HostScope(orgId, GrantedToUserId: Collaborator));
    }

    private static Booking Stay(Property property, Guest guest, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal basePrice)
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

    private static Payment Pay(Booking booking, PaymentStatus status, decimal amount, decimal refunded = 0m, DateTime? processed = null, DateTime? created = null)
    {
        return new Payment
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Status = status,
            Amount = amount,
            RefundedAmount = refunded,
            ProcessedAt = processed,
            CreatedAt = created ?? processed ?? At(10, 1, 10),
            UpdatedAt = created ?? processed ?? At(10, 1, 10),
        };
    }

    private static HostDashboardQuery Next30Days(bool compare = false, bool collected = true, Guid? propertyId = null) =>
        new(HostDashboardPeriodKind.Next30Days, null, propertyId, compare, collected);

    // --- The next 30 days -------------------------------------------------------------------------------

    [Fact]
    public async Task Next30Days_ThePeriodStartsTodayInRome_AndTheFiguresAreWorkedOutOverIt()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.OrgWide, Next30Days());

        Assert.Equal(HostDashboardPeriodKind.Next30Days, kpis.Period.Kind);
        Assert.Equal(Day(10, 9), kpis.Period.From);
        Assert.Equal(Day(11, 8), kpis.Period.To);
        Assert.Equal(Day(10, 9), kpis.TodayInRome);
        // Two short-rent properties: the one in long-term mode is neither counted nor in the nights available.
        Assert.Equal(2, kpis.PropertyCount);
        Assert.Equal(new HostDashboardOccupancy(7, 60, 0), kpis.Occupancy);
        // A 300 + B 2 of 3 nights 200 + C 2 of 4 nights 200.
        Assert.Equal(700m, kpis.Revenue);
        Assert.Equal(3, kpis.RevenueStayCount);
        // Only A came from the booking site (Direct); B was entered by the host, C is an Airbnb stay.
        Assert.Equal(new HostDashboardDirectShare(1, 3), kpis.DirectShare);
        Assert.Equal(1m / 3m, kpis.DirectShare.Rate);
        Assert.Null(kpis.Previous);
    }

    [Fact]
    public async Task Next30Days_Collected_IsByCash_NetOfRefunds_OnRomeDays()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.OrgWide, Next30Days());

        // 300 + (200 - 50 refunded) + 100 (no instant of settlement: created on the 25th) + 10 (00:30 on the 9th in Rome).
        // Not the fully refunded one, not the failed one, not the 20 of 23:59 on the 8th, not another org's 999.
        Assert.Equal(new HostDashboardCollected(560m, 4), kpis.Collected);
    }

    [Fact]
    public async Task Collected_IsLeftOutUnlessAsked_AndCostsNoQueryThen()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.OrgWide, Next30Days(collected: false, compare: true));

        Assert.Null(kpis.Collected);
        Assert.Null(kpis.Previous!.Collected);
        // The accrual figures are there all the same.
        Assert.Equal(700m, kpis.Revenue);
    }

    [Fact]
    public async Task Compare_TheFiguresOfThePeriodBefore_WithTheSameNumberOfNights()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.OrgWide, Next30Days(compare: true));

        var previous = kpis.Previous!;
        Assert.Equal(HostDashboardPeriodKind.Next30Days, previous.Period.Kind);
        Assert.Equal(Day(9, 9), previous.Period.From);
        Assert.Equal(Day(10, 9), previous.Period.To);
        Assert.Equal(kpis.Period.Nights, previous.Period.Nights);
        // F 5 nights of the 20th of September on the Trullo and the night of the 8th of B: 6 of 60.
        Assert.Equal(new HostDashboardOccupancy(6, 60, 0), previous.Occupancy);
        // F 500 + B 1 of 3 nights 100.
        Assert.Equal(600m, previous.Revenue);
        Assert.Equal(2, previous.RevenueStayCount);
        Assert.Equal(new HostDashboardDirectShare(1, 2), previous.DirectShare);
        Assert.Equal(0.5m, previous.DirectShare.Rate);
        // 500 on the 30th of September and 20 at 23:59 on the 8th of October in Rome.
        Assert.Equal(new HostDashboardCollected(520m, 2), previous.Collected);
        // The current figures are not touched by asking for the comparison.
        Assert.Equal(700m, kpis.Revenue);
        Assert.Equal(560m, kpis.Collected!.Amount);
    }

    [Fact]
    public async Task Month_ComparesWithTheSameNumberOfDaysBeforeIt()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(
            world.OrgWide, new HostDashboardQuery(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), Compare: true));

        Assert.Equal(31, kpis.Period.Nights);
        // October: A 300 and B 300; the long-term property leaves the nights available (2 x 31).
        Assert.Equal(600m, kpis.Revenue);
        Assert.Equal(new HostDashboardOccupancy(6, 62, 0), kpis.Occupancy);
        Assert.Equal(Day(8, 31), kpis.Previous!.Period.From);
        Assert.Equal(Day(10, 1), kpis.Previous.Period.To);
        Assert.Equal(31, kpis.Previous.Period.Nights);
    }

    [Fact]
    public async Task TheOldSignature_IsTheSameWithoutTheNewFigures()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.OrgWide, HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1));

        Assert.Equal(600m, kpis.Revenue);
        Assert.Equal(2, kpis.RevenueStayCount);
        Assert.Null(kpis.Collected);
        Assert.Null(kpis.Previous);
    }

    // --- One property -----------------------------------------------------------------------------------

    [Fact]
    public async Task PropertyFilter_NarrowsEveryFigureToTheProperty()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.OrgWide, Next30Days(propertyId: world.Trullo.Id));

        Assert.Equal(1, kpis.PropertyCount);
        // A (3 nights) and C (2 nights) of 30.
        Assert.Equal(new HostDashboardOccupancy(5, 30, 0), kpis.Occupancy);
        Assert.Equal(500m, kpis.Revenue);
        Assert.Equal(new HostDashboardDirectShare(1, 2), kpis.DirectShare);
        // The payments of A (300) and C (100); the failed and the refunded ones of A do not count.
        Assert.Equal(new HostDashboardCollected(400m, 2), kpis.Collected);
        Assert.All(kpis.RecentBookings, stay => Assert.Equal(world.Trullo.Id, stay.PropertyId));
    }

    [Fact]
    public async Task PropertyFilter_APropertyThatIsNotTheCallers_IsNotFound_WhoeverItBelongsTo()
    {
        var world = await SeedAsync();
        var service = Dashboard(world.Db);

        // Another org's property, an unknown one, and (restricted) a property of the org the caller was not given.
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetKpisAsync(world.OrgWide, Next30Days(propertyId: world.Foreign.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetKpisAsync(world.OrgWide, Next30Days(propertyId: Guid.NewGuid())));
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetKpisAsync(world.Restricted, Next30Days(propertyId: world.CasaBianca.Id)));
        Assert.Equal("PropertyNotFound", error.MessageKey);
    }

    // --- A collaborator limited to some properties ----------------------------------------------------

    [Fact]
    public async Task Restricted_TheCollaboratorSeesTheFiguresOfItsPropertyOnly_MoneyIncluded()
    {
        var world = await SeedAsync();

        var kpis = await Dashboard(world.Db).GetKpisAsync(world.Restricted, Next30Days(compare: true));

        Assert.Equal(1, kpis.PropertyCount);
        Assert.Equal(new HostDashboardOccupancy(5, 30, 0), kpis.Occupancy);
        Assert.Equal(500m, kpis.Revenue);
        Assert.Equal(new HostDashboardDirectShare(1, 2), kpis.DirectShare);
        Assert.Equal(new HostDashboardCollected(400m, 2), kpis.Collected);
        // The period before: F on the Trullo only, and its payment; the night and the 20 of Casa Bianca are not the collaborator's.
        Assert.Equal(new HostDashboardOccupancy(5, 30, 0), kpis.Previous!.Occupancy);
        Assert.Equal(500m, kpis.Previous.Revenue);
        Assert.Equal(new HostDashboardCollected(500m, 1), kpis.Previous.Collected);
    }

    // --- The stays of the day --------------------------------------------------------------------------

    [Fact]
    public async Task TodayStays_ArrivalsDeparturesAndTheCheckInsToCome_OnTheRomeDay()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.Add(property);
        db.Guests.Add(guest);

        var arrivesToday = Stay(property, guest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 9), Day(10, 12), 300m);
        // 01:30 of the 9th in Rome, stored as the instant 23:30Z of the 8th: still an arrival of today, never of yesterday.
        var arrivesAfterMidnight = Stay(property, guest, BookingStatus.Confirmed, BookingSource.Manual, At(10, 8, 23, 30), Day(10, 11), 200m);
        var arrivedToday = Stay(property, guest, BookingStatus.CheckedIn, BookingSource.Airbnb, Day(10, 9), Day(10, 10), 100m);
        arrivedToday.ArrivedAt = At(10, 9, 8);
        var leavesToday = Stay(property, guest, BookingStatus.CheckedIn, BookingSource.Direct, Day(10, 6), Day(10, 9), 300m);
        var tomorrow = Stay(property, guest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 12), 300m);
        var later = Stay(property, guest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 20), Day(10, 22), 300m);
        var cancelled = Stay(property, guest, BookingStatus.Cancelled, BookingSource.Direct, Day(10, 9), Day(10, 12), 300m);
        var request = Stay(property, guest, BookingStatus.Pending, BookingSource.Direct, Day(10, 9), Day(10, 12), 300m);
        db.Bookings.AddRange(arrivesToday, arrivesAfterMidnight, arrivedToday, leavesToday, tomorrow, later, cancelled, request);
        await db.SaveChangesAsync();

        var stays = await Dashboard(db).GetTodayStaysAsync(new HostScope(orgId));

        Assert.Equal(Day(10, 9), stays.TodayInRome);
        Assert.Equal(
            new[] { arrivesToday.Id, arrivesAfterMidnight.Id, arrivedToday.Id }.Order(),
            stays.Arrivals.Items.Select(s => s.BookingId).Order());
        Assert.Equal(3, stays.Arrivals.Count);
        Assert.Equal([leavesToday.Id], stays.Departures.Items.Select(s => s.BookingId));
        // After today: the arrivals of today are in the first list, a cancelled stay and a request in neither.
        Assert.Equal([tomorrow.Id, later.Id], stays.Upcoming.Items.Select(s => s.BookingId));
        Assert.Equal(2, stays.Upcoming.Count);

        var registered = stays.Arrivals.Items.Single(s => s.BookingId == arrivedToday.Id);
        Assert.Equal(At(10, 9, 8), registered.ArrivedAt);
        Assert.Equal(BookingSource.Airbnb, registered.Source);
        Assert.Equal(2, registered.NumberOfGuests);
        Assert.Equal(arrivedToday.BookingCode, registered.BookingCode);
        Assert.Equal(Day(10, 9), stays.Arrivals.Items.Single(s => s.BookingId == arrivesAfterMidnight.Id).CheckInDate);
    }

    [Fact]
    public async Task TodayStays_ACollaboratorSeesTheStaysOfItsPropertyOnly()
    {
        var world = await SeedAsync();
        // One arrival today on each of the two properties.
        var guest = await world.Db.Guests.FirstAsync(g => g.OrgId == world.OrgId);
        world.Db.Bookings.AddRange(
            Stay(world.Trullo, guest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 9), Day(10, 12), 300m),
            Stay(world.CasaBianca, guest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 9), Day(10, 12), 300m));
        await world.Db.SaveChangesAsync();

        var restricted = await Dashboard(world.Db).GetTodayStaysAsync(world.Restricted);
        var orgWide = await Dashboard(world.Db).GetTodayStaysAsync(world.OrgWide);

        Assert.Equal([world.Trullo.Id], restricted.Arrivals.Items.Select(s => s.PropertyId));
        Assert.Equal(new[] { world.Trullo.Id, world.CasaBianca.Id }.Order(), orgWide.Arrivals.Items.Select(s => s.PropertyId).Order());
    }
}
