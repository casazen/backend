using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A2-36: the bookings KPIs of the property detail count by status and by Europe/Rome calendar date (fixed clock, FD-06):
/// the next check-in is never a cancelled booking, an arrival of today is upcoming until the host registers it and then
/// in progress, and "today" is the Rome date also between 22:00 and 24:00 UTC. Plus the pure rules of the dashboard
/// (PC-16): revenue pro rata per night and the period of the query string.
/// </summary>
public class PropertyBookingsSummaryTests
{
    private static DateTime Date(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Booking Stay(BookingStatus status, DateTime checkIn, DateTime checkOut) =>
        new() { Status = status, CheckInDate = checkIn, CheckOutDate = checkOut };

    [Fact]
    public void BuildBookingsSummary_CancelledBookingBeforeTheNextOne_IsNeverTheNextCheckIn()
    {
        var today = Date(6, 10);
        var bookings = new[]
        {
            Stay(BookingStatus.Cancelled, Date(6, 11), Date(6, 12)),
            Stay(BookingStatus.Pending, Date(6, 11), Date(6, 13)),
            Stay(BookingStatus.Confirmed, Date(6, 14), Date(6, 16)),
        };

        var summary = PropertyService.BuildBookingsSummary(bookings, today);

        Assert.Equal(Date(6, 14), summary.NextCheckIn);
        Assert.Equal(Date(6, 16), summary.NextCheckOut);
        Assert.Equal(1, summary.UpcomingBookings);
        Assert.Equal(1, summary.TotalBookings);
    }

    [Fact]
    public void BuildBookingsSummary_ArrivalOfToday_IsUpcomingUntilRegisteredThenInProgress()
    {
        var today = Date(6, 10);

        var confirmed = PropertyService.BuildBookingsSummary([Stay(BookingStatus.Confirmed, today, Date(6, 12))], today);
        var arrived = PropertyService.BuildBookingsSummary([Stay(BookingStatus.CheckedIn, today, Date(6, 12))], today);

        Assert.Equal((1, 0), (confirmed.UpcomingBookings, confirmed.ActiveBookings));
        Assert.Equal(today, confirmed.NextCheckIn);
        Assert.Equal((0, 1), (arrived.UpcomingBookings, arrived.ActiveBookings));
        Assert.Null(arrived.NextCheckIn);
    }

    [Fact]
    public void BuildBookingsSummary_StaysInProgress_CountTheDepartureDayAndTheUnregisteredArrivals()
    {
        var today = Date(6, 10);
        var bookings = new[]
        {
            Stay(BookingStatus.CheckedIn, Date(6, 8), today),              // leaves today
            Stay(BookingStatus.Confirmed, Date(6, 9), Date(6, 12)),        // arrived yesterday, not registered (CO-08)
            Stay(BookingStatus.CheckedIn, Date(6, 5), Date(6, 9)),         // should have left: not in progress
            Stay(BookingStatus.Cancelled, Date(6, 9), Date(6, 12)),
            Stay(BookingStatus.CheckedOut, Date(6, 8), today),
        };

        var summary = PropertyService.BuildBookingsSummary(bookings, today);

        Assert.Equal(2, summary.ActiveBookings);
        Assert.Equal(0, summary.UpcomingBookings);
        Assert.Equal(4, summary.TotalBookings);
        Assert.Equal(today, summary.NextCheckOut);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_At2330UtcTheRomeDayHasChanged_ArrivalOfTheRomeDayIsUpcoming()
    {
        var propertyId = Guid.NewGuid();
        var property = new Property { Id = propertyId, OwnerId = "auth0|owner", Name = "Villa", IsActive = true };
        // 15 June 23:30 UTC = 16 June 01:30 in Rome: yesterday's arrival (15 June) is no longer upcoming, 16 June is.
        property.Bookings.Add(Stay(BookingStatus.Confirmed, Date(6, 15), Date(6, 17)));
        property.Bookings.Add(Stay(BookingStatus.Confirmed, Date(6, 16), Date(6, 18)));
        // A check-in stored as an instant (23:30 UTC of 15 June) is a check-in of 16 June in Rome.
        property.Bookings.Add(Stay(BookingStatus.Confirmed, new DateTime(2026, 6, 15, 23, 30, 0, DateTimeKind.Utc), Date(6, 20)));
        var repository = new Mock<IPropertyRepository>();
        repository.Setup(r => r.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 23, 30, 0, TimeSpan.Zero));
        var service = new PropertyService(
            repository.Object,
            Mock.Of<IPropertyComplianceStatusService>(),
            new CinDeadlineCalendar(Options.Create(new CinOptions()), clock),
            Mock.Of<ILogger<PropertyService>>(),
            clock);

        var summary = (await service.GetPropertyDetailAsync(propertyId)).BookingsSummary;

        Assert.Equal(2, summary.UpcomingBookings);
        Assert.Equal(1, summary.ActiveBookings);
        Assert.Equal(Date(6, 16), summary.NextCheckIn);
    }

    [Fact]
    public void ArrivesOn_InstantAt2330Utc_FallsOnTheNextRomeDate()
    {
        var arrivesOn16 = StayKpiRules.ArrivesOn(Date(6, 16)).Compile();
        var arrivesOn15 = StayKpiRules.ArrivesOn(Date(6, 15)).Compile();
        var late = Stay(BookingStatus.Confirmed, new DateTime(2026, 6, 15, 23, 30, 0, DateTimeKind.Utc), Date(6, 18));

        Assert.True(arrivesOn16(late));
        Assert.False(arrivesOn15(late));
        Assert.True(arrivesOn16(Stay(BookingStatus.CheckedIn, Date(6, 16), Date(6, 18))));
        Assert.False(arrivesOn16(Stay(BookingStatus.Cancelled, Date(6, 16), Date(6, 18))));
        Assert.Equal(new DateOnly(2026, 6, 16), RomeCalendar.DateInRome(StayKpiRules.RomeDateOf(late.CheckInDate)));
    }

    [Theory]
    [InlineData(6, 1, 6, 5, 400, 400)]      // all 4 nights in June
    [InlineData(5, 29, 6, 2, 300, 75)]      // 1 of 4 nights in June
    [InlineData(6, 28, 7, 3, 500, 300)]     // 3 of 5 nights in June
    [InlineData(5, 1, 5, 31, 900, 0)]       // all in May
    public void RevenueInPeriod_StayAndJune_ProRataPerNight(
        int inMonth, int inDay, int outMonth, int outDay, int basePrice, int expected)
    {
        var revenue = StayKpiRules.RevenueInPeriod(basePrice, Date(inMonth, inDay), Date(outMonth, outDay), Date(6, 1), Date(7, 1));

        Assert.Equal((decimal)expected, revenue);
    }

    [Fact]
    public void RevenueInPeriod_StayWithoutNights_CountsNothing()
    {
        Assert.Equal(0m, StayKpiRules.RevenueInPeriod(300m, Date(6, 5), Date(6, 5), Date(6, 1), Date(7, 1)));
    }

    [Theory]
    [InlineData(null, null, HostDashboardPeriodKind.Month, null)]
    [InlineData("month", "2026-05", HostDashboardPeriodKind.Month, "2026-05-01")]
    [InlineData("Month", null, HostDashboardPeriodKind.Month, null)]
    [InlineData("last30days", null, HostDashboardPeriodKind.Last30Days, null)]
    [InlineData(null, "2026-12", HostDashboardPeriodKind.Month, "2026-12-01")]
    public void TryParsePeriod_ValidValues_AreAccepted(string? period, string? month, HostDashboardPeriodKind kind, string? monthStart)
    {
        Assert.True(DashboardController.TryParsePeriod(period, month, out var parsedKind, out var parsedMonth));
        Assert.Equal(kind, parsedKind);
        Assert.Equal(monthStart is null ? null : DateOnly.Parse(monthStart), parsedMonth);
    }

    [Theory]
    [InlineData("year", null)]
    [InlineData("1", null)]
    [InlineData("-1", null)]
    [InlineData(null, "2026-13")]
    [InlineData(null, "06-2026")]
    [InlineData(null, "2026-06-01")]
    [InlineData(null, "1999-12")]
    [InlineData("Last30Days", "2026-06")]
    public void TryParsePeriod_InvalidValues_AreRefused(string? period, string? month)
    {
        Assert.False(DashboardController.TryParsePeriod(period, month, out _, out _));
    }

    [Fact]
    public void Period_Last30Days_EndsWithTonight()
    {
        var period = HostDashboardPeriod.ForLast30Days(Date(6, 10));

        Assert.Equal(Date(5, 12), period.From);
        Assert.Equal(Date(6, 11), period.To);
        Assert.Equal(30, period.Nights);
    }
}
