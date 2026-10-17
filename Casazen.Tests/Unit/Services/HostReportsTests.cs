using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-10: the three reports are the Home cut up, never a second rule. For the same days the revenue by property adds up to the revenue
/// of the Home and each property to what the Home gives when it is asked for that property; the stays by channel and the nights by
/// property are the Home's too. Numbers worked out by hand, October 2026.
/// </summary>
public class HostReportsTests
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static readonly DateOnly October = new(2026, 10, 1);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Booking Stay(Property property, Guid guestId, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal basePrice) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        GuestId = guestId,
        Status = status,
        Source = source,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
        BasePrice = basePrice,
        TotalPrice = basePrice,
    };

    [Fact]
    public async Task Reports_AreTheNumbersOfTheHome_ForTheSameDays_ByPropertyAndInTotal()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr10", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr10", "Casa Bianca");
        var paused = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr10", "Villa in pausa");
        paused.IsActive = false;
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-sr10", "Villa Altrove");
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var foreignGuest = new Guest { OrgId = otherOrgId, FirstName = "Luca", LastName = "Neri", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.AddRange(trullo, casaBianca, paused, foreign);
        db.Guests.AddRange(guest, foreignGuest);

        // Trullo: A 3 nights, 300; C 30 October to 3 November, 400 for 4 nights (2 of them in October: 200); D cancelled, nothing.
        // Casa Bianca: B 3 nights, 300. The paused villa still has its money: E 3 nights, 150. Another org's stay is nobody's here.
        db.Bookings.AddRange(
            Stay(trullo, guest.Id, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 13), 300m),
            Stay(trullo, guest.Id, BookingStatus.Confirmed, BookingSource.Airbnb, Day(10, 30), Day(11, 3), 400m),
            Stay(trullo, guest.Id, BookingStatus.Cancelled, BookingSource.Direct, Day(10, 12), Day(10, 14), 250m),
            Stay(casaBianca, guest.Id, BookingStatus.CheckedIn, BookingSource.Manual, Day(10, 8), Day(10, 11), 300m),
            Stay(paused, guest.Id, BookingStatus.CheckedOut, BookingSource.BookingCom, Day(10, 5), Day(10, 8), 150m),
            Stay(foreign, foreignGuest.Id, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 20), 1000m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);
        var scope = new HostScope(orgId);
        var home = await service.GetKpisAsync(scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October));
        var query = new HostReportQuery(October, new DateOnly(2026, 10, 31));

        var revenue = await service.GetRevenueByPropertyAsync(scope, query);
        var channels = await service.GetBookingsByChannelAsync(scope, query);
        var occupancy = await service.GetOccupancyByPropertyAsync(scope, query);

        // The revenue: the Home's, and the Home's for each property.
        Assert.Equal(950m, home.Revenue);
        Assert.Equal(home.Revenue, revenue.Amount);
        Assert.Equal(home.RevenueStayCount, revenue.StayCount);
        Assert.Equal(
            new[] { ("Casa Bianca", 300m), ("Trullo", 500m), ("Villa in pausa", 150m) },
            revenue.Rows.Select(r => (r.PropertyName, r.Amount)).ToArray());
        foreach (var row in revenue.Rows)
        {
            var ofProperty = await service.GetKpisAsync(scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October, row.PropertyId));
            Assert.Equal(ofProperty.Revenue, row.Amount);
        }

        // The channels cut the same stays: the same revenue, and the Home's direct stays are the Direct row.
        Assert.Equal(home.Revenue, channels.Revenue);
        Assert.Equal(home.RevenueStayCount, channels.BookingCount);
        Assert.Equal(home.DirectShare.DirectStays, channels.Rows.Single(r => r.Source == BookingSource.Direct).BookingCount);

        // The nights: the Home's occupancy is the sum of the rows, over the same (active) properties.
        Assert.Equal(home.Occupancy, occupancy.Total);
        Assert.Equal(new[] { "Casa Bianca", "Trullo" }, occupancy.Rows.Select(r => r.PropertyName).ToArray());
    }
}
