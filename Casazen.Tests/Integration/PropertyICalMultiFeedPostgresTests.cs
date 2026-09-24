using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-11 (A2-11, A2-20) on PostgreSQL: a property imports Airbnb and Booking.com at once, each feed owns its blocks
/// (public availability and booking check of BK-05 see both), removing a feed frees only its dates, the import URL is
/// encrypted in the database and masked in the API, and another org sees none of it. Downloads go through the
/// scripted client of <see cref="PropertyICalSyncPostgresTests.Factory"/>: no network.
/// </summary>
public class PropertyICalMultiFeedPostgresTests : IClassFixture<PropertyICalSyncPostgresTests.Factory>
{
    private const string DataProtectionPayloadPrefix = "CfDJ8";

    private readonly PropertyICalSyncPostgresTests.Factory _factory;

    public PropertyICalMultiFeedPostgresTests(PropertyICalSyncPostgresTests.Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task SyncFeedAsync_AirbnbAndBookingFeeds_BothDateSeriesAreTakenForTheSiteAndTheBookings()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc11-two-feeds");
        var from = PublicAvailabilityPostgresTests.NextYear(10, 1);
        var run = Guid.NewGuid().ToString("N");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");

        var airbnb = await AddFeedAsync(owner, property.Id, $"https://www.airbnb.it/calendar/ical/{run}.ics?s=a1", from, 2, 4);
        var booking = await AddFeedAsync(owner, property.Id, $"https://admin.booking.com/ical/{run}.ics?t=b2", from, 7, 9);
        await SyncAsync(airbnb.Id);
        await SyncAsync(booking.Id);

        // Public site (BK-05): the nights of both OTAs are taken.
        Assert.Equal(
            [Day(from, 2), Day(from, 3), Day(from, 7), Day(from, 8)],
            await BookedDatesAsync(property.Id, from, from.AddDays(14)));

        // Booking check (the same PropertyOccupancy rule): both series refuse a stay, the night between is free.
        await using var scope = _factory.Services.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<PropertyICalSyncService>();
        Assert.True(await sync.HasOverlappingBlockAsync(property.Id, from.AddDays(3), from.AddDays(4)));
        Assert.True(await sync.HasOverlappingBlockAsync(property.Id, from.AddDays(8), from.AddDays(10)));
        Assert.False(await sync.HasOverlappingBlockAsync(property.Id, from.AddDays(4), from.AddDays(7)));

        var feeds = await GetFeedsAsync(owner, property.Id);
        Assert.Equal(["Airbnb", "BookingCom"], feeds.Select(f => f.GetProperty("channel").GetString()));
        Assert.All(feeds, f => Assert.Equal("Success", f.GetProperty("lastImportStatus").GetString()));
        Assert.All(feeds, f => Assert.Equal(1, f.GetProperty("blockCount").GetInt32()));
    }

    [PostgresFact]
    public async Task RemoveFeed_OneOfTwoFeeds_DeletesOnlyItsBlocksAndFreesOnlyItsDates()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc11-remove");
        var from = PublicAvailabilityPostgresTests.NextYear(11, 1);
        var run = Guid.NewGuid().ToString("N");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        var airbnb = await AddFeedAsync(owner, property.Id, $"https://www.airbnb.it/calendar/ical/{run}.ics?s=r1", from, 1, 3);
        var booking = await AddFeedAsync(owner, property.Id, $"https://admin.booking.com/ical/{run}.ics?t=r2", from, 5, 6);
        await SyncAsync(airbnb.Id);
        await SyncAsync(booking.Id);

        var response = await owner.DeleteAsync($"/api/properties/{property.Id}/ical/feeds/{airbnb.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([Day(from, 5)], await BookedDatesAsync(property.Id, from, from.AddDays(10)));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(booking.Id, (await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == property.Id)).Id);
        var blocks = await db.CalendarBlocks.AsNoTracking().Where(b => b.PropertyId == property.Id).ToListAsync();
        Assert.Equal(booking.Id, Assert.Single(blocks).FeedId);
    }

    // A2-20: the import URL (with the Airbnb token) is never in clear in the database nor in any API answer.
    [PostgresFact]
    public async Task AddFeed_ImportUrl_IsEncryptedInTheDatabaseAndMaskedInTheApi()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pc11-crypt-{Guid.NewGuid():N}");
        const string token = "7f3e9c1d2b4a6f8e0d1c3b5a7e9f2d4c";
        var url = $"https://www.airbnb.it/calendar/ical/{Guid.NewGuid():N}.ics?s={token}";
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");

        var added = await owner.PostAsJsonAsync($"/api/properties/{property.Id}/ical/feeds", new { importUrl = url, label = "Airbnb" });

        Assert.Equal(HttpStatusCode.Accepted, added.StatusCode);
        var feedId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Database
            .SqlQuery<string>($"SELECT \"ImportUrl\" AS \"Value\" FROM \"PropertyICalFeeds\" WHERE \"Id\" = {feedId}")
            .SingleAsync();
        Assert.NotEqual(url, stored);
        Assert.StartsWith(DataProtectionPayloadPrefix, stored);
        Assert.DoesNotContain(token, stored);
        Assert.DoesNotContain("airbnb", stored, StringComparison.OrdinalIgnoreCase);
        // Read through EF, the sync gets the URL back.
        Assert.Equal(url, (await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == feedId)).ImportUrl);

        foreach (var path in new[] { "ical/feeds", "ical/status" })
        {
            var raw = await owner.GetStringAsync($"/api/properties/{property.Id}/{path}");
            Assert.DoesNotContain(token, raw);
            Assert.DoesNotContain(stored, raw);
            Assert.DoesNotContain("importUrl\"", raw);
            using var json = JsonDocument.Parse(raw);
            var feeds = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement : json.RootElement.GetProperty("feeds");
            Assert.Equal("www.airbnb.it/…2d4c", Assert.Single(feeds.EnumerateArray()).GetProperty("maskedImportUrl").GetString());
        }
    }

    // TN-3: a host of another org sees no feed and changes nothing (404, like a missing property).
    [PostgresFact]
    public async Task FeedEndpoints_HostOfAnotherOrg_Return404AndChangeNothing()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pc11-owner-{Guid.NewGuid():N}");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        var feed = await AddFeedAsync(
            owner, property.Id, $"https://www.airbnb.it/calendar/ical/{Guid.NewGuid():N}.ics", PublicAvailabilityPostgresTests.NextYear(9, 1), 1, 2);
        await SyncAsync(feed.Id);

        var otherHost = $"auth0|pc11-other-{Guid.NewGuid():N}";
        var otherProperty = await _factory.SeedPropertyAsync(otherHost);
        using var other = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");
        var basePath = $"/api/properties/{property.Id}/ical";

        var responses = new[]
        {
            await other.GetAsync($"{basePath}/feeds"),
            await other.GetAsync($"{basePath}/status"),
            await other.PostAsJsonAsync($"{basePath}/feeds", new { importUrl = "https://www.airbnb.it/calendar/ical/x.ics" }),
            await other.PostAsync($"{basePath}/feeds/{feed.Id}/sync", null),
            await other.DeleteAsync($"{basePath}/feeds/{feed.Id}"),
        };
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));

        // Its own property with the other org's feed id: the feed is not one of its feeds.
        var crossFeed = await other.DeleteAsync($"/api/properties/{otherProperty.Id}/ical/feeds/{feed.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossFeed.StatusCode);
        Assert.Equal(ICalFeedErrorCodes.NotFound, (await crossFeed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feeds = await db.PropertyICalFeeds.AsNoTracking().Where(f => f.PropertyId == property.Id).ToListAsync();
        Assert.Equal(feed.Id, Assert.Single(feeds).Id);
        Assert.Equal(1, await db.CalendarBlocks.CountAsync(b => b.FeedId == feed.Id));
    }

    // Adds a feed through the API and scripts its calendar: one event from day startOffset to endOffset of from.
    private async Task<FeedRef> AddFeedAsync(HttpClient owner, Guid propertyId, string url, DateTime from, int startOffset, int endOffset)
    {
        var response = await owner.PostAsJsonAsync($"/api/properties/{propertyId}/ical/feeds", new { importUrl = url });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        _factory.Feeds[url] = () =>
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n"
            + $"BEGIN:VEVENT\r\nUID:{id:N}@ota\r\nDTSTART;VALUE=DATE:{from.AddDays(startOffset):yyyyMMdd}\r\n"
            + $"DTEND;VALUE=DATE:{from.AddDays(endOffset):yyyyMMdd}\r\nSUMMARY:Reserved\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        return new FeedRef(id, url);
    }

    // What the queued Hangfire job does (the test host mocks the job client).
    private async Task SyncAsync(Guid feedId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncFeedAsync(feedId);
    }

    private static async Task<List<JsonElement>> GetFeedsAsync(HttpClient client, Guid propertyId)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/ical/feeds");
        return body.EnumerateArray().ToList();
    }

    private async Task<List<string>> BookedDatesAsync(Guid propertyId, DateTime from, DateTime to)
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(PublicAvailabilityPostgresTests.AvailabilityPath(propertyId, from, to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()!).ToList();
    }

    private static string Day(DateTime from, int offset) => from.AddDays(offset).ToString("yyyy-MM-dd");

    private sealed record FeedRef(Guid Id, string Url);
}
