using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// MO-06 (A6-11, A6-10, A2-16): <c>GET /api/bookings/calendar</c> reads its range as stay dates (both ends included,
/// never moved to the property time zone), returns stay dates, and gives the iCal blocks the channel of their feed.
/// The dates are fixed and every booking is confirmed: nothing depends on today.
/// </summary>
public class BookingCalendarIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";
    private readonly CasazenWebApplicationFactory _factory;

    public BookingCalendarIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetCalendar_MonthOfStayDates_ReturnsTheStaysAndBlocksOfEveryDayOfTheMonth()
    {
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        var otherProperty = await _factory.SeedPropertyAsync(hostId);

        var arrivesOnLastDay = await SeedBookingAsync(property, Day(9, 30), Day(10, 2));
        var leavesOnFirstDay = await SeedBookingAsync(property, Day(8, 28), Day(9, 1));
        var leftBefore = await SeedBookingAsync(property, Day(8, 25), Day(8, 31));
        var arrivesAfter = await SeedBookingAsync(property, Day(10, 1), Day(10, 3));
        var cancelled = await SeedBookingAsync(property, Day(9, 10), Day(9, 12), BookingStatus.Cancelled);
        var ofOtherProperty = await SeedBookingAsync(otherProperty, Day(9, 10), Day(9, 12));

        var airbnb = await SeedFeedAsync(property, ICalFeedChannel.Airbnb, label: null);
        var bookingCom = await SeedFeedAsync(property, ICalFeedChannel.BookingCom, label: "Booking.com - camera 2");
        var airbnbBlock = await SeedBlockAsync(property, airbnb, Day(9, 30), Day(10, 3));
        var labelledBlock = await SeedBlockAsync(property, bookingCom, Day(9, 5), Day(9, 7));
        var blockAfter = await SeedBlockAsync(property, bookingCom, Day(10, 1), Day(10, 4));
        var manualBlock = await SeedBlockAsync(property, feedId: null, Day(9, 15), Day(9, 16));

        using var client = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var response = await client.GetAsync(
            $"/api/bookings/calendar?propertyId={property.Id}&startDate=2026-09-01&endDate=2026-09-30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        var bookingIds = IdsOfType(items, "booking");
        var blockIds = IdsOfType(items, "ical-block");

        // By arrival day: the departure on 01/09 first, the arrivals on the last day of the month last.
        Assert.Equal(
            [leavesOnFirstDay, labelledBlock, manualBlock, arrivesOnLastDay, airbnbBlock],
            items.Select(i => i.GetProperty("id").GetGuid()).ToList());
        Assert.DoesNotContain(leftBefore, bookingIds);
        Assert.DoesNotContain(arrivesAfter, bookingIds);
        Assert.DoesNotContain(cancelled, bookingIds);
        Assert.DoesNotContain(ofOtherProperty, bookingIds);
        Assert.Equal(new HashSet<Guid> { airbnbBlock, labelledBlock, manualBlock }, blockIds.ToHashSet());
        Assert.DoesNotContain(blockAfter, blockIds);
        Assert.Equal(
            new HashSet<Guid> { arrivesOnLastDay, leavesOnFirstDay },
            body.GetProperty("bookings").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToHashSet());

        // Stay dates, the same day for every client: never shifted by the property time zone (Europe/Rome).
        var lastDayStay = Item(items, arrivesOnLastDay);
        Assert.Equal("2026-09-30T00:00:00", lastDayStay.GetProperty("startDate").GetString());
        Assert.Equal("2026-10-02T00:00:00", lastDayStay.GetProperty("endDate").GetString());
        Assert.Equal("Anna Verdi", lastDayStay.GetProperty("guestName").GetString());
        Assert.Equal("Confirmed", lastDayStay.GetProperty("status").GetString());
        var lastDayBooking = body.GetProperty("bookings").EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == arrivesOnLastDay);
        Assert.Equal("2026-09-30T00:00:00", lastDayBooking.GetProperty("checkInDate").GetString());

        // Blocks carry the channel of their feed (and its label), never a guest.
        var airbnbItem = Item(items, airbnbBlock);
        Assert.Equal("Airbnb", airbnbItem.GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Null, airbnbItem.GetProperty("feedLabel").ValueKind);
        Assert.Equal(JsonValueKind.Null, airbnbItem.GetProperty("guestName").ValueKind);
        Assert.Equal("2026-09-30T00:00:00", airbnbItem.GetProperty("startDate").GetString());
        Assert.Equal("2026-10-03T00:00:00", airbnbItem.GetProperty("endDate").GetString());
        var labelledItem = Item(items, labelledBlock);
        Assert.Equal("BookingCom", labelledItem.GetProperty("channel").GetString());
        Assert.Equal("Booking.com - camera 2", labelledItem.GetProperty("feedLabel").GetString());
        Assert.Equal(JsonValueKind.Null, Item(items, manualBlock).GetProperty("channel").ValueKind);
    }

    [Fact]
    public async Task GetCalendar_EndBeforeStart_Returns400WithStableCode()
    {
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);

        using var client = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var response = await client.GetAsync(
            $"/api/bookings/calendar?propertyId={property.Id}&startDate=2026-09-30&endDate=2026-09-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(BookingErrorCodes.CalendarRangeInvalid, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetCalendar_PropertyOfAnotherHost_Returns404WithoutItsStays()
    {
        var property = await _factory.SeedPropertyAsync(NewHostId());
        await SeedBookingAsync(property, Day(9, 10), Day(9, 12));

        var otherHostId = NewHostId();
        await _factory.SeedOrgForOwnerAsync(otherHostId);

        using var client = _factory.CreateAuthenticatedClient(otherHostId, HostRole);
        var response = await client.GetAsync(
            $"/api/bookings/calendar?propertyId={property.Id}&startDate=2026-09-01&endDate=2026-09-30");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Anna", await response.Content.ReadAsStringAsync());
    }

    private static string NewHostId() => $"auth0|mo06-{Guid.NewGuid():N}";

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static List<Guid> IdsOfType(IEnumerable<JsonElement> items, string type) =>
        items.Where(i => i.GetProperty("type").GetString() == type).Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static JsonElement Item(IEnumerable<JsonElement> items, Guid id) =>
        items.Single(i => i.GetProperty("id").GetGuid() == id);

    private async Task<Guid> SeedBookingAsync(
        Property property,
        DateTime checkIn,
        DateTime checkOut,
        BookingStatus status = BookingStatus.Confirmed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Guid> SeedFeedAsync(Property property, ICalFeedChannel channel, string? label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Channel = channel,
            Label = label,
            ImportUrl = $"https://example.com/{Guid.NewGuid():N}.ics",
        };
        db.PropertyICalFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.Id;
    }

    private async Task<Guid> SeedBlockAsync(Property property, Guid? feedId, DateTime start, DateTime end)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var block = new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            FeedId = feedId,
            Source = feedId is null ? CalendarBlockSource.Manual : CalendarBlockSource.ICalImport,
            ExternalUid = feedId is null ? null : $"uid-{Guid.NewGuid():N}",
            StartUtc = start,
            EndUtc = end,
            Summary = "Reserved",
        };
        db.CalendarBlocks.Add(block);
        await db.SaveChangesAsync();
        return block.Id;
    }
}
