using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-16 (A2-29) on real PostgreSQL, through the whole pipeline (auth, TN-3 scope, tenant filter): the host dashboard
/// computes its KPIs on the server for a period, with the clock of the app fixed (<see cref="FakeTimeProvider"/>, FD-06):
/// occupancy as occupied / available property-nights (same nights as <c>PropertyOccupancy</c>), revenue of the confirmed
/// stays pro rata per night, today's arrivals and departures on the Europe/Rome day, upcoming check-ins without the
/// cancelled bookings, the iCal feeds of the org; another org sees none of it.
/// </summary>
public class HostDashboardPostgresTests : IClassFixture<HostDashboardPostgresTests.Factory>
{
    private const string HostRole = "PropertyOwner";

    // June 2026: a month of 30 days. 09:00 UTC is the same date in Rome.
    private static readonly DateTimeOffset JuneTenth = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly Factory _factory;

    public HostDashboardPostgresTests(Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetKpis_TwoPropertiesInAMonthOf30Days_27NightsTakenOf60Is45Percent()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        var first = await _factory.SeedPropertyAsync(hostId);
        var second = await _factory.SeedPropertyAsync(hostId);
        // First property: 10 nights of a stay (1-10) and 5 of a block imported from Airbnb (20-24) = 15.
        await SeedBookingAsync(first, BookingStatus.Confirmed, June(1), June(11));
        await SeedBlockAsync(first, CalendarBlockSource.ICalImport, June(20), June(25));
        // Second property: 6 nights checked in (8-13) and the 6 June nights of a stay ending in July (25-30) = 12.
        await SeedBookingAsync(second, BookingStatus.CheckedIn, June(8), June(14));
        await SeedBookingAsync(second, BookingStatus.Confirmed, June(25), new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));
        // Not taken: a cancelled booking, a stay of May.
        await SeedBookingAsync(second, BookingStatus.Cancelled, June(15), June(18));
        await SeedBookingAsync(first, BookingStatus.CheckedOut, new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
        // Another org, fully booked in June: none of its nights nor its property counts here.
        var otherProperty = await _factory.SeedPropertyAsync(NewHostId());
        await SeedBookingAsync(otherProperty, BookingStatus.Confirmed, June(1), new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var kpis = await GetKpisAsync(host, "");

        Assert.Equal("Month", kpis.GetProperty("period").GetProperty("kind").GetString());
        Assert.Equal("2026-06-01", kpis.GetProperty("period").GetProperty("from").GetString());
        Assert.Equal("2026-06-30", kpis.GetProperty("period").GetProperty("to").GetString());
        Assert.Equal(30, kpis.GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(2, kpis.GetProperty("propertyCount").GetInt32());
        var occupancy = kpis.GetProperty("occupancy");
        Assert.Equal(27, occupancy.GetProperty("occupiedNights").GetInt32());
        Assert.Equal(60, occupancy.GetProperty("availableNights").GetInt32());
        Assert.Equal(0, occupancy.GetProperty("closedNights").GetInt32());
        Assert.Equal(0.45m, occupancy.GetProperty("rate").GetDecimal());
    }

    [PostgresFact]
    public async Task GetKpis_ManualBlock_LeavesTheAvailabilityInsteadOfCountingAsOccupied()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        await SeedBookingAsync(property, BookingStatus.Confirmed, June(1), June(8));
        // Owner stay 10-14 (4 nights), closed by hand: neither occupied nor available.
        await SeedBlockAsync(property, CalendarBlockSource.Manual, June(10), June(14));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var occupancy = (await GetKpisAsync(host, "?period=Month&month=2026-06")).GetProperty("occupancy");

        Assert.Equal(7, occupancy.GetProperty("occupiedNights").GetInt32());
        Assert.Equal(26, occupancy.GetProperty("availableNights").GetInt32());
        Assert.Equal(4, occupancy.GetProperty("closedNights").GetInt32());
        Assert.Equal(7m / 26m, occupancy.GetProperty("rate").GetDecimal(), 10);
    }

    [PostgresFact]
    public async Task GetKpis_Revenue_ConfirmedStaysProRataPerNightOfThePeriodWithoutTouristTax()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        // 4 nights in June: 400. The tourist tax (for the comune) is not revenue.
        await SeedBookingAsync(property, BookingStatus.Confirmed, June(1), June(5), basePrice: 400m, touristTax: 20m);
        // 29 May - 1 June: 1 of 4 nights in June = 75, the other 3 in May = 225.
        await SeedBookingAsync(property, BookingStatus.CheckedOut, new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc), June(2), basePrice: 300m);
        // 28 June - 2 July: 3 of 5 nights in June = 300.
        await SeedBookingAsync(property, BookingStatus.CheckedIn, June(28), new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc), basePrice: 500m);
        // Not confirmed: no revenue.
        await SeedBookingAsync(property, BookingStatus.Pending, June(10), June(12), basePrice: 1000m);
        await SeedBookingAsync(property, BookingStatus.Cancelled, June(15), June(17), basePrice: 1000m);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var june = (await GetKpisAsync(host, "?period=Month")).GetProperty("revenue");
        var may = (await GetKpisAsync(host, "?period=Month&month=2026-05")).GetProperty("revenue");

        Assert.Equal(775m, june.GetProperty("amount").GetDecimal());
        Assert.Equal(3, june.GetProperty("stayCount").GetInt32());
        Assert.Equal("EUR", june.GetProperty("currency").GetString());
        Assert.Equal(225m, may.GetProperty("amount").GetDecimal());
        Assert.Equal(1, may.GetProperty("stayCount").GetInt32());
    }

    [PostgresFact]
    public async Task GetKpis_Last30Days_EndsTodayInRomeIncluded()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        // The period starts on 12 May: the nights of 10-11 May are out, those of 12-13 May in.
        await SeedBookingAsync(property, BookingStatus.CheckedOut, new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc));
        await SeedBookingAsync(property, BookingStatus.CheckedOut, new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc));
        // Tonight (10 June) is in the period, tomorrow night is not.
        await SeedBookingAsync(property, BookingStatus.Confirmed, June(10), June(12));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var kpis = await GetKpisAsync(host, "?period=last30days");

        Assert.Equal("Last30Days", kpis.GetProperty("period").GetProperty("kind").GetString());
        Assert.Equal("2026-05-12", kpis.GetProperty("period").GetProperty("from").GetString());
        Assert.Equal("2026-06-10", kpis.GetProperty("period").GetProperty("to").GetString());
        Assert.Equal(3, kpis.GetProperty("occupancy").GetProperty("occupiedNights").GetInt32());
        Assert.Equal(30, kpis.GetProperty("occupancy").GetProperty("availableNights").GetInt32());
    }

    [PostgresFact]
    public async Task GetKpis_ArrivalAt2330Utc_IsAnArrivalOfTheRomeDayItFallsOn()
    {
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        // Check-in stored as an instant: 15 June 23:30 UTC is already 16 June 01:30 in Rome.
        var lateArrival = await SeedBookingAsync(property, BookingStatus.Confirmed, new DateTime(2026, 6, 15, 23, 30, 0, DateTimeKind.Utc), June(19));
        var arrival16 = await SeedBookingAsync(property, BookingStatus.Confirmed, June(16), June(18));
        var arrival15 = await SeedBookingAsync(property, BookingStatus.Confirmed, June(13), June(15));
        var departure16 = await SeedBookingAsync(property, BookingStatus.CheckedIn, June(14), June(16));
        await SeedBookingAsync(property, BookingStatus.Cancelled, June(16), June(20));
        await SeedBookingAsync(property, BookingStatus.Pending, June(16), June(17));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        // 15 June 10:00 UTC (12:00 in Rome): the 23:30 UTC arrival is tomorrow's, an upcoming check-in dated 16 June.
        _factory.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        var afternoon = await GetKpisAsync(host, "");

        Assert.Equal("2026-06-15", afternoon.GetProperty("today").GetString());
        Assert.Empty(Ids(afternoon.GetProperty("arrivalsToday")));
        Assert.Equal([arrival15], Ids(afternoon.GetProperty("departuresToday")));
        var upcoming = afternoon.GetProperty("upcomingCheckIns").GetProperty("items").EnumerateArray().ToList();
        var late = Assert.Single(upcoming, i => i.GetProperty("bookingId").GetGuid() == lateArrival);
        Assert.Equal("2026-06-16", late.GetProperty("checkInDate").GetString());

        // 15 June 23:30 UTC = 16 June 01:30 in Rome: today is 16 June, not the UTC date.
        _factory.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 15, 23, 30, 0, TimeSpan.Zero));
        var night = await GetKpisAsync(host, "");

        Assert.Equal("2026-06-16", night.GetProperty("today").GetString());
        Assert.Equal(new[] { lateArrival, arrival16 }.Order(), Ids(night.GetProperty("arrivalsToday")).Order());
        Assert.Equal(2, night.GetProperty("arrivalsToday").GetProperty("count").GetInt32());
        Assert.Equal([departure16], Ids(night.GetProperty("departuresToday")));
    }

    [PostgresFact]
    public async Task GetKpis_UpcomingCheckIns_ConfirmedFromTodaySoonestFirstNeverCancelled()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        await SeedBookingAsync(property, BookingStatus.Cancelled, June(11), June(12));
        await SeedBookingAsync(property, BookingStatus.Pending, June(11), June(12));
        var later = await SeedBookingAsync(property, BookingStatus.Confirmed, June(20), June(22));
        var today = await SeedBookingAsync(property, BookingStatus.Confirmed, June(10), June(11));
        var next = await SeedBookingAsync(property, BookingStatus.Confirmed, June(12), June(14));
        // Arrived already today, or a past stay: not a check-in to come.
        await SeedBookingAsync(property, BookingStatus.CheckedIn, June(10), June(13));
        await SeedBookingAsync(property, BookingStatus.Confirmed, June(1), June(3));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var upcoming = (await GetKpisAsync(host, "")).GetProperty("upcomingCheckIns");

        Assert.Equal(3, upcoming.GetProperty("count").GetInt32());
        Assert.Equal([today, next, later], Ids(upcoming));
    }

    [PostgresFact]
    public async Task GetKpisAndIcalFeeds_TwoOrgs_EachSeesOnlyItsOwnData()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostA = NewHostId();
        var propertyA = await _factory.SeedPropertyAsync(hostA);
        var bookingA = await SeedBookingAsync(propertyA, BookingStatus.Confirmed, June(10), June(15), basePrice: 500m);
        var feedA = await SeedFeedAsync(propertyA, PropertyICalImportStatus.Success, lastError: null);
        var hostB = NewHostId();
        var propertyB = await _factory.SeedPropertyAsync(hostB);
        await SeedBookingAsync(propertyB, BookingStatus.Confirmed, June(10), June(12), basePrice: 200m);
        using var clientA = _factory.CreateAuthenticatedClient(hostA, HostRole);
        using var clientB = _factory.CreateAuthenticatedClient(hostB, HostRole);
        var hostC = NewHostId();
        await _factory.SeedOrgForOwnerAsync(hostC);
        using var clientC = _factory.CreateAuthenticatedClient(hostC, HostRole);

        var a = await GetKpisAsync(clientA, "");
        var b = await GetKpisAsync(clientB, "");
        var empty = await GetKpisAsync(clientC, "");
        var feedsB = await clientB.GetFromJsonAsync<JsonElement>("/api/dashboard/ical-feeds");
        var feedsA = await clientA.GetFromJsonAsync<JsonElement>("/api/dashboard/ical-feeds");

        Assert.Equal(500m, a.GetProperty("revenue").GetProperty("amount").GetDecimal());
        Assert.Equal([bookingA], Ids(a.GetProperty("arrivalsToday")));
        Assert.Equal([bookingA], a.GetProperty("recentBookings").EnumerateArray().Select(i => i.GetProperty("bookingId").GetGuid()));
        Assert.Equal(200m, b.GetProperty("revenue").GetProperty("amount").GetDecimal());
        Assert.DoesNotContain(bookingA, Ids(b.GetProperty("arrivalsToday")));
        Assert.DoesNotContain(bookingA, b.GetProperty("recentBookings").EnumerateArray().Select(i => i.GetProperty("bookingId").GetGuid()));
        Assert.Equal(0, feedsB.GetArrayLength());
        Assert.Equal([feedA], feedsA.EnumerateArray().Select(f => f.GetProperty("feedId").GetGuid()));
        // An org without properties: zeroes, no rate, nothing listed.
        Assert.Equal(0, empty.GetProperty("propertyCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("occupancy").GetProperty("rate").ValueKind);
        Assert.Equal(0m, empty.GetProperty("revenue").GetProperty("amount").GetDecimal());
        Assert.Empty(empty.GetProperty("recentBookings").EnumerateArray());
    }

    [PostgresFact]
    public async Task GetIcalFeeds_FeedsOfTheHost_LastSyncStatusAndTranslatedErrorFailuresFirstWithoutUrl()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        var property = await _factory.SeedPropertyAsync(hostId);
        var ok = await SeedFeedAsync(property, PropertyICalImportStatus.Success, lastError: null);
        var failing = await SeedFeedAsync(property, PropertyICalImportStatus.Failure, ICalErrorCodes.Unreachable);
        // A raw exception message saved before FD-16 is never shown as is.
        var legacy = await SeedFeedAsync(property, PropertyICalImportStatus.Failure, "System.Net.Http.HttpRequestException: 10.0.0.1 refused");
        var never = await SeedFeedAsync(property, status: null, lastError: null);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.GetAsync("/api/dashboard/ical-feeds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("airbnb.it/calendar", raw);
        Assert.DoesNotContain("importUrl", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.0.0.1", raw);
        var feeds = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();
        Assert.Equal(4, feeds.Count);
        Assert.Equal(new[] { failing, legacy }.Order(), feeds.Take(2).Select(f => f.GetProperty("feedId").GetGuid()).Order());
        var failure = feeds.Single(f => f.GetProperty("feedId").GetGuid() == failing);
        Assert.Equal("Failure", failure.GetProperty("lastImportStatus").GetString());
        Assert.Equal(ICalErrorCodes.Unreachable, failure.GetProperty("lastErrorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("lastError").GetString()));
        Assert.NotEqual(ICalErrorCodes.Unreachable, failure.GetProperty("lastError").GetString());
        Assert.Equal(ICalErrorCodes.SyncFailed, feeds.Single(f => f.GetProperty("feedId").GetGuid() == legacy).GetProperty("lastErrorCode").GetString());
        var success = feeds.Single(f => f.GetProperty("feedId").GetGuid() == ok);
        Assert.Equal("Success", success.GetProperty("lastImportStatus").GetString());
        Assert.Equal(property.Name, success.GetProperty("propertyName").GetString());
        Assert.Equal("Airbnb", success.GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Null, feeds.Single(f => f.GetProperty("feedId").GetGuid() == never).GetProperty("lastImportStatus").ValueKind);
    }

    [PostgresFact]
    public async Task GetKpis_InvalidPeriod_Returns400WithStableCode()
    {
        _factory.Clock.SetUtcNow(JuneTenth);
        var hostId = NewHostId();
        await _factory.SeedPropertyAsync(hostId);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        foreach (var query in new[] { "?period=year", "?period=1", "?month=2026-13", "?period=Last30Days&month=2026-06", "?month=06-2026" })
        {
            var response = await host.GetAsync($"/api/dashboard/kpis{query}");

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{query}: {response.StatusCode}");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("dashboard_invalid_period", problem.GetProperty("code").GetString());
        }
    }

    [PostgresFact]
    public async Task GetKpis_SupplierWithoutHostPermissions_IsForbidden()
    {
        using var supplier = _factory.CreateAuthenticatedClient(NewHostId(), "Supplier");

        var kpis = await supplier.GetAsync("/api/dashboard/kpis");
        var feeds = await supplier.GetAsync("/api/dashboard/ical-feeds");

        Assert.Equal(HttpStatusCode.Forbidden, kpis.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, feeds.StatusCode);
    }

    private static DateTime June(int day) => new(2026, 6, day, 0, 0, 0, DateTimeKind.Utc);

    private static string NewHostId() => $"auth0|pc16-{Guid.NewGuid():N}";

    private static async Task<JsonElement> GetKpisAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/dashboard/kpis{query}");
        Assert.True(response.IsSuccessStatusCode, $"{query}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<Guid> Ids(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("bookingId").GetGuid()).ToList();

    private int _guestNumber;

    /// <summary>A booking inserted directly (the booking API refuses past check-ins and overlapping dates).</summary>
    private async Task<Guid> SeedBookingAsync(
        Property property,
        BookingStatus status,
        DateTime checkIn,
        DateTime checkOut,
        decimal basePrice = 300m,
        decimal touristTax = 0m)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = $"Verdi {Interlocked.Increment(ref _guestNumber)}",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            Status = status,
            Source = BookingSource.Manual,
            BasePrice = basePrice,
            TouristTax = touristTax,
            TotalPrice = basePrice + touristTax,
            CreatedAt = _factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(-Interlocked.Increment(ref _guestNumber)),
            UpdatedAt = _factory.Clock.GetUtcNow().UtcDateTime,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task SeedBlockAsync(Property property, CalendarBlockSource source, DateTime start, DateTime end)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Source = source,
            ExternalUid = source == CalendarBlockSource.ICalImport ? $"{Guid.NewGuid():N}@airbnb.com" : null,
            StartUtc = start,
            EndUtc = end,
            Summary = "Reserved",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedFeedAsync(Property property, PropertyICalImportStatus? status, string? lastError)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Channel = ICalFeedChannel.Airbnb,
            ImportUrl = $"https://www.airbnb.it/calendar/ical/{Guid.NewGuid():N}.ics?s=secret",
            CreatedAt = _factory.Clock.GetUtcNow().UtcDateTime,
            LastImportAt = status is null ? null : _factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(-15),
            LastImportStatus = status,
            LastError = lastError,
        };
        db.PropertyICalFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.Id;
    }

    /// <summary>The app with its clock under the test's control (FD-06): "today" never depends on when the tests run.</summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        public FakeTimeProvider Clock { get; } = new(JuneTenth);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }
}
