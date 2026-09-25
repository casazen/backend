using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Casazen.Tests.Unit;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-21 (decision D7, GC-AC9) on PostgreSQL: the host turns a block imported from an OTA calendar into a confirmed OTA
/// stay; from it the check-in link, the cockpit and the arrival work as for any stay, its nights count once, the export
/// never publishes it, a block is converted once, another org sees nothing, and a later sync never cancels the stay: a
/// block gone from the feed (or with other dates) marks it "da verificare" and alerts the host once. The host clock is a
/// fixed <see cref="FakeTimeProvider"/> ("today" is the first day of the block); downloads are scripted, no network.
/// </summary>
public class OtaStayPostgresTests : IClassFixture<OtaStayPostgresTests.Factory>
{
    private const string HostRole = "PropertyOwner";

    // "Today" for the host: 2 October of next year at 10:00 in Rome (FD-06, fixed clock).
    private static readonly DateTime Today = PublicAvailabilityPostgresTests.NextYear(10, 2);
    private static readonly DateTimeOffset Now = new(Today.AddHours(8), TimeSpan.Zero);

    private readonly Factory _factory;

    public OtaStayPostgresTests(Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task CreateFromBlock_ImportedAirbnbBlock_CreatesConfirmedAirbnbStayWithCheckInLinkCockpitAndArrival()
    {
        var seeded = await SeedImportedBlockAsync("co21-convert");
        using var host = Host(seeded.Property);

        var response = await ConvertAsync(host, seeded.BlockId, new { firstName = "Mario", lastName = "Rossi", email = "mario.rossi@example.com", numberOfGuests = 2 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stay = await response.Content.ReadFromJsonAsync<JsonElement>();
        var stayId = stay.GetProperty("id").GetGuid();
        Assert.Equal("Confirmed", stay.GetProperty("status").GetString());
        Assert.Equal("Airbnb", stay.GetProperty("source").GetString());
        Assert.Equal(seeded.From, stay.GetProperty("checkInDate").GetDateTime().Date);
        Assert.Equal(seeded.To, stay.GetProperty("checkOutDate").GetDateTime().Date);
        Assert.Equal(2, stay.GetProperty("numberOfGuests").GetInt32());
        Assert.Equal(seeded.FeedId, stay.GetProperty("icalFeedId").GetGuid());
        Assert.Equal("Camera 2", stay.GetProperty("channelLabel").GetString());
        Assert.Equal(seeded.BlockId, stay.GetProperty("channelBlock").GetProperty("id").GetGuid());
        // No price is made up: the host gave none.
        Assert.Equal(0m, stay.GetProperty("totalPrice").GetDecimal());
        Assert.Equal("mario.rossi@example.com", stay.GetProperty("guest").GetProperty("email").GetString());

        await WithDbAsync(async db =>
        {
            var block = await db.CalendarBlocks.SingleAsync(b => b.Id == seeded.BlockId);
            var booking = await db.Bookings.Include(b => b.Guest).SingleAsync(b => b.Id == stayId);
            Assert.Equal(stayId, block.BookingId);
            Assert.Equal(block.ExternalUid, booking.ExternalId);
            Assert.Equal(seeded.Property.OrgId, booking.OrgId);
            Assert.Equal(seeded.Property.OrgId, booking.Guest.OrgId);
        });

        // Check-in link of the guest (CO-09), from the stay.
        var link = await host.PostAsync($"/api/bookings/{stayId}/checkin/link", null);
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var linkBody = await link.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("https://casazen-app.vercel.app/", linkBody.GetProperty("checkInLink").GetString());
        Assert.Equal("NotRequested", linkBody.GetProperty("emailStatus").GetString());

        // Cockpit (CO-04): the guest data of the stay arriving today are to complete.
        var summary = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");
        Assert.Contains(
            summary.GetProperty("guestCheckInsIncomplete").GetProperty("items").EnumerateArray(),
            item => item.GetProperty("bookingId").GetGuid() == stayId);

        // "Registra arrivo" (CO-08) on the arrival day.
        var arrival = await host.PostAsync($"/api/bookings/{stayId}/check-in", null);
        Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);
        Assert.Equal("CheckedIn", (await arrival.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task CreateFromBlock_PublicAvailability_SameNightsCountedOnce()
    {
        var seeded = await SeedImportedBlockAsync("co21-availability");
        var before = await BookedDatesAsync(seeded.Property.Id, Today, Today.AddDays(10));
        using var host = Host(seeded.Property);

        var response = await ConvertAsync(host, seeded.BlockId, Guest("Anna", "Verdi"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stayId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var after = await BookedDatesAsync(seeded.Property.Id, Today, Today.AddDays(10));
        Assert.Equal(new[] { 0, 1, 2 }.Select(d => Today.AddDays(d).ToString("yyyy-MM-dd")).ToList(), before);
        Assert.Equal(before, after);
        Assert.Equal(after.Count, after.Distinct().Count());

        // PropertyOccupancy (BK-05): each night is taken by one record, the stay, no longer by its block too.
        await WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.CalendarBlocks.CountAsync(PropertyOccupancy.BlockTakesNightIn(seeded.Property.Id, seeded.From, seeded.To)));
            Assert.Equal(stayId, await db.Bookings
                .Where(PropertyOccupancy.BookingTakesNightIn(seeded.Property.Id, seeded.From, seeded.To))
                .Where(CheckoutHolds.OccupiesDates(null))
                .Select(b => b.Id)
                .SingleAsync());
        });

        // The calendar of the host shows the stay once, not the block as well.
        var calendar = await host.GetFromJsonAsync<JsonElement>(
            $"/api/bookings/calendar?propertyId={seeded.Property.Id}&startDate={Today:yyyy-MM-dd}&endDate={Today.AddDays(10):yyyy-MM-dd}&timezone=UTC");
        var items = calendar.GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal("booking", item.GetProperty("type").GetString());
        Assert.Equal(seeded.FeedId, item.GetProperty("icalFeedId").GetGuid());
    }

    [PostgresFact]
    public async Task PublicExport_OtaStayCreatedFromBlock_IsNotExported()
    {
        var seeded = await SeedImportedBlockAsync("co21-export");
        using var host = Host(seeded.Property);
        var response = await ConvertAsync(host, seeded.BlockId, Guest("Luca", "Neri"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stayId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var exportUrl = (await host.GetFromJsonAsync<JsonElement>($"/api/properties/{seeded.Property.Id}/ical/export-url"))
            .GetProperty("exportUrl").GetString()!;
        using var anonymous = _factory.CreateClient();
        var ics = await anonymous.GetStringAsync(new Uri(exportUrl).AbsolutePath);

        // PC-12, no echo: neither the stay (source Airbnb) nor its imported block goes back to the OTAs.
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.DoesNotContain("BEGIN:VEVENT", ics);
        Assert.DoesNotContain(stayId.ToString(), ics);
        Assert.DoesNotContain(seeded.From.ToString("yyyyMMdd"), ics);
        Assert.DoesNotContain("Luca", ics);
    }

    [PostgresFact]
    public async Task CreateFromBlock_Twice_Returns409AndKeepsOneStay()
    {
        var seeded = await SeedImportedBlockAsync("co21-twice");
        using var host = Host(seeded.Property);
        Assert.Equal(HttpStatusCode.Created, (await ConvertAsync(host, seeded.BlockId, Guest("Mario", "Rossi"))).StatusCode);

        var secondEmail = $"second.{Guid.NewGuid():N}@example.com";
        var second = await ConvertAsync(host, seeded.BlockId, new { firstName = "Paolo", lastName = "Bianchi", email = secondEmail });

        await AssertProblemAsync(second, HttpStatusCode.Conflict, "ota_stay_block_already_converted");
        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Bookings.CountAsync(b => b.PropertyId == seeded.Property.Id));
            Assert.Equal(0, await db.Guests.CountAsync(g => g.Email == secondEmail));
        });
    }

    [PostgresFact]
    public async Task CreateFromBlock_OverlappingExistingBooking_Returns409WithoutStay()
    {
        var seeded = await SeedImportedBlockAsync("co21-overlap");
        await SeedManualBookingAsync(seeded.Property, seeded.From.AddDays(1), seeded.To.AddDays(2));
        using var host = Host(seeded.Property);
        var email = $"overlap.{Guid.NewGuid():N}@example.com";

        var response = await ConvertAsync(host, seeded.BlockId, new { firstName = "Mario", lastName = "Rossi", email });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ota_stay_block_overlaps_booking");
        await WithDbAsync(async db =>
        {
            Assert.Null((await db.CalendarBlocks.SingleAsync(b => b.Id == seeded.BlockId)).BookingId);
            Assert.Equal(0, await db.Guests.CountAsync(g => g.Email == email));
            Assert.Equal(1, await db.Bookings.CountAsync(b => b.PropertyId == seeded.Property.Id));
        });
    }

    [PostgresFact]
    public async Task CreateFromBlock_OtherChannelFeed_RequiresAnOtaSourceAndEndedBlockIsRefused()
    {
        var seeded = await SeedImportedBlockAsync("co21-other", host: "calendar.example.org", feedLabel: null);
        using var host = Host(seeded.Property);

        var withoutSource = await ConvertAsync(host, seeded.BlockId, Guest("Mario", "Rossi"));
        await AssertProblemAsync(withoutSource, (HttpStatusCode)422, "ota_stay_source_required");
        var notAnOta = await ConvertAsync(host, seeded.BlockId, new { firstName = "Mario", lastName = "Rossi", email = "m@example.com", source = "Manual" });
        await AssertProblemAsync(notAnOta, (HttpStatusCode)422, "ota_stay_source_required");

        var withSource = await ConvertAsync(
            host, seeded.BlockId, new { firstName = "Mario", lastName = "Rossi", email = "m@example.com", source = "Expedia", totalPrice = 480.5m });
        Assert.Equal(HttpStatusCode.Created, withSource.StatusCode);
        var stay = await withSource.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expedia", stay.GetProperty("source").GetString());
        Assert.Equal(480.5m, stay.GetProperty("totalPrice").GetDecimal());

        // A stay already over (check-out before today in Rome) is not converted.
        var ended = await SeedImportedBlockAsync("co21-ended", from: Today.AddDays(-5), nights: 3);
        using var endedHost = Host(ended.Property);
        await AssertProblemAsync(await ConvertAsync(endedHost, ended.BlockId, Guest("Mario", "Rossi")), (HttpStatusCode)422, "ota_stay_block_ended");
    }

    [PostgresFact]
    public async Task Sync_BlockGoneFromFeed_StayMarkedToCheckNotCancelledAndHostAlertedOnce()
    {
        var seeded = await SeedImportedBlockAsync("co21-removed");
        using var host = Host(seeded.Property);
        var stayId = await ConvertedStayAsync(host, seeded.BlockId);

        // The reservation is no longer in the Airbnb calendar (cancelled or moved on the channel).
        _factory.Feeds[seeded.Url] = () => Calendar();
        await SyncAsync(seeded.FeedId);

        await WithDbAsync(async db =>
        {
            var stay = await db.Bookings.SingleAsync(b => b.Id == stayId);
            Assert.Equal(BookingStatus.Confirmed, stay.Status);
            Assert.Equal(seeded.From, stay.CheckInDate.Date);
            Assert.Equal(OtaStayReviewReason.BlockRemoved, stay.OtaReviewReason);
            Assert.Equal(0, await db.CalendarBlocks.CountAsync(b => b.FeedId == seeded.FeedId));
        });
        // The dates stay taken on the booking site: CasaZen does not release a stay by itself.
        Assert.Equal(3, (await BookedDatesAsync(seeded.Property.Id, Today, Today.AddDays(10))).Count);

        var email = Assert.Single(_factory.Emails, e => e.Template == "host-ota-stay-review" && e.Content.HtmlBody.Contains(stayId.ToString()));
        Assert.Contains("Airbnb (Camera 2)", email.Content.Subject);
        Assert.Single(_factory.Pushes, p => p.BookingId == stayId && p.Type == NotificationService.OtaStayReviewPushType);

        // The next syncs find nothing new: no second alert.
        await SyncAsync(seeded.FeedId);
        Assert.Single(_factory.Pushes, p => p.BookingId == stayId);

        var detail = await host.GetFromJsonAsync<JsonElement>($"/api/bookings/{stayId}");
        Assert.Equal("BlockRemoved", detail.GetProperty("otaReviewReason").GetString());
        var list = await host.GetFromJsonAsync<JsonElement>($"/api/bookings?propertyId={seeded.Property.Id}");
        Assert.Equal("BlockRemoved", list.EnumerateArray().Single().GetProperty("otaReviewReason").GetString());

        var resolved = await host.PostAsJsonAsync($"/api/bookings/{stayId}/ota-review/resolve", new { });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await resolved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("otaReviewReason").ValueKind);
        await WithDbAsync(async db => Assert.Null((await db.Bookings.SingleAsync(b => b.Id == stayId)).OtaReviewReason));
    }

    [PostgresFact]
    public async Task Sync_BlockDatesChanged_StayKeepsItsDatesUntilTheHostAppliesTheChannelOnes()
    {
        var seeded = await SeedImportedBlockAsync("co21-moved");
        using var host = Host(seeded.Property);
        var stayId = await ConvertedStayAsync(host, seeded.BlockId);

        // Same reservation (same UID), one day later on the channel.
        _factory.Feeds[seeded.Url] = () => Calendar(Event(seeded.Uid, seeded.From.AddDays(1), seeded.To.AddDays(1)));
        await SyncAsync(seeded.FeedId);

        await WithDbAsync(async db =>
        {
            var stay = await db.Bookings.SingleAsync(b => b.Id == stayId);
            Assert.Equal(seeded.From, stay.CheckInDate.Date);
            Assert.Equal(seeded.To, stay.CheckOutDate.Date);
            Assert.Equal(OtaStayReviewReason.BlockDatesChanged, stay.OtaReviewReason);
        });
        // Both ranges stay taken until the host decides: the stay's nights and the channel's new ones.
        Assert.Equal(4, (await BookedDatesAsync(seeded.Property.Id, Today, Today.AddDays(10))).Count);
        var email = Assert.Single(_factory.Emails, e => e.Template == "host-ota-stay-review" && e.Content.HtmlBody.Contains(stayId.ToString()));
        Assert.Contains(seeded.To.AddDays(1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), email.Content.HtmlBody);

        var detail = await host.GetFromJsonAsync<JsonElement>($"/api/bookings/{stayId}");
        Assert.Equal("BlockDatesChanged", detail.GetProperty("otaReviewReason").GetString());
        Assert.Equal(seeded.From.AddDays(1), detail.GetProperty("channelBlock").GetProperty("startDate").GetDateTime().Date);

        var applied = await host.PostAsJsonAsync($"/api/bookings/{stayId}/ota-review/resolve", new { applyChannelDates = true });

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        await WithDbAsync(async db =>
        {
            var stay = await db.Bookings.SingleAsync(b => b.Id == stayId);
            Assert.Equal(seeded.From.AddDays(1), stay.CheckInDate.Date);
            Assert.Equal(seeded.To.AddDays(1), stay.CheckOutDate.Date);
            Assert.Null(stay.OtaReviewReason);
            // The block stands for its stay again: its nights count once.
            Assert.Equal(0, await db.CalendarBlocks.CountAsync(PropertyOccupancy.BlockTakesNightIn(seeded.Property.Id, Today, Today.AddDays(10))));
        });
        Assert.Equal(3, (await BookedDatesAsync(seeded.Property.Id, Today, Today.AddDays(10))).Count);
    }

    [PostgresFact]
    public async Task CreateAndResolve_HostOfAnotherOrg_Return404()
    {
        var seeded = await SeedImportedBlockAsync("co21-tenant");
        using var owner = Host(seeded.Property);
        var stayId = await ConvertedStayAsync(owner, seeded.BlockId);
        var other = await _factory.SeedPropertyAsync($"auth0|co21-other-{Guid.NewGuid():N}");
        using var intruder = _factory.CreateAuthenticatedClient(other.OwnerId, HostRole);

        await AssertProblemAsync(await ConvertAsync(intruder, seeded.BlockId, Guest("Eva", "Neri")), HttpStatusCode.NotFound, "ical_block_not_found");
        var resolve = await intruder.PostAsJsonAsync($"/api/bookings/{stayId}/ota-review/resolve", new { });
        Assert.Equal(HttpStatusCode.NotFound, resolve.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/bookings/{stayId}")).StatusCode);
    }

    private HttpClient Host(Property property) => _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

    private static object Guest(string firstName, string lastName) =>
        new { firstName, lastName, email = $"{firstName.ToLowerInvariant()}.{Guid.NewGuid():N}@example.com" };

    private static Task<HttpResponseMessage> ConvertAsync(HttpClient client, Guid blockId, object body) =>
        client.PostAsJsonAsync($"/api/ical-blocks/{blockId}/ota-stay", body);

    private static async Task<Guid> ConvertedStayAsync(HttpClient host, Guid blockId)
    {
        var response = await ConvertAsync(host, blockId, new { firstName = "Mario", lastName = "Rossi", email = "mario@example.com" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    /// <summary>
    /// A published property with an import feed (added through the API, channel taken from <paramref name="host"/>) whose
    /// one event, from <paramref name="from"/> (today by default) for <paramref name="nights"/> nights, became a block.
    /// </summary>
    private async Task<SeededBlock> SeedImportedBlockAsync(
        string name,
        string host = "www.airbnb.it",
        string? feedLabel = "Camera 2",
        DateTime? from = null,
        int nights = 3)
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, name);
        var start = from ?? Today;
        var to = start.AddDays(nights);
        var url = $"https://{host}/calendar/ical/{Guid.NewGuid():N}.ics?s=co21";
        var uid = $"{Guid.NewGuid():N}@{host}";
        _factory.Feeds[url] = () => Calendar(Event(uid, start, to));

        using var owner = Host(property);
        var added = await owner.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/feeds", new { importUrl = url, label = feedLabel });
        Assert.Equal(HttpStatusCode.Accepted, added.StatusCode);
        var feedId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await SyncAsync(feedId);

        var blockId = Guid.Empty;
        await WithDbAsync(async db => blockId = await db.CalendarBlocks.Where(b => b.FeedId == feedId).Select(b => b.Id).SingleAsync());
        return new SeededBlock(property, feedId, blockId, uid, url, start, to);
    }

    private async Task SeedManualBookingAsync(Property property, DateTime checkIn, DateTime checkOut)
    {
        await WithDbAsync(async db =>
        {
            var guest = new Guest { OrgId = property.OrgId, FirstName = "Giulia", LastName = "Bianchi", Email = $"giulia.{Guid.NewGuid():N}@example.com" };
            db.Guests.Add(guest);
            db.Bookings.Add(new Booking
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                GuestId = guest.Id,
                CheckInDate = checkIn,
                CheckOutDate = checkOut,
                NumberOfGuests = 1,
                NumberOfAdults = 1,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Manual,
            });
            await db.SaveChangesAsync();
        });
    }

    private async Task SyncAsync(Guid feedId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncFeedAsync(feedId);
    }

    private async Task<List<string>> BookedDatesAsync(Guid propertyId, DateTime from, DateTime to)
    {
        using var anonymous = _factory.CreateClient();
        var body = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/public/bookings/property/{propertyId}/availability?startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}");
        return body.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()!).ToList();
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Airbnb Inc//Hosting Calendar//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Event(string uid, DateTime from, DateTime to) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{from:yyyyMMdd}\r\nDTEND;VALUE=DATE:{to:yyyyMMdd}\r\n"
        + "SUMMARY:Reserved\r\nEND:VEVENT\r\n";

    private sealed record SeededBlock(Property Property, Guid FeedId, Guid BlockId, string Uid, string Url, DateTime From, DateTime To);

    /// <summary>
    /// The integration host with a fixed clock (<see cref="Now"/>), the download client replaced by <see cref="Feeds"/>
    /// (URL → body), and the emails and pushes recorded instead of sent.
    /// </summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        public ConcurrentDictionary<string, Func<string>> Feeds { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<(string? To, EmailContent Content, string Template)> Emails { get; } = new();

        public ConcurrentQueue<PushNotificationPayload> Pushes { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISafeExternalHttpClient>();
                services.AddSingleton<ISafeExternalHttpClient>(new ScriptedExternalHttpClient(Feeds));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                services.RemoveAll<IEmailQueue>();
                services.AddSingleton<IEmailQueue>(new RecordingEmailQueue(Emails));
                services.RemoveAll<IPushNotificationService>();
                services.AddSingleton<IPushNotificationService>(new RecordingPushService(Pushes));
            });
        }
    }

    private sealed class ScriptedExternalHttpClient(ConcurrentDictionary<string, Func<string>> feeds) : ISafeExternalHttpClient
    {
        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) =>
            feeds.TryGetValue(url, out var body)
                ? Task.Run(body, cancellationToken)
                : throw new ExternalFetchException(ExternalFetchFailure.Unreachable, "not scripted");
    }

    private sealed class RecordingEmailQueue(ConcurrentQueue<(string? To, EmailContent Content, string Template)> emails) : IEmailQueue
    {
        public bool Enqueue(string? to, EmailContent content, string template)
        {
            emails.Enqueue((to, content, template));
            return true;
        }
    }

    private sealed class RecordingPushService(ConcurrentQueue<PushNotificationPayload> pushes) : IPushNotificationService
    {
        public Task SendToUserAsync(string userId, PushNotificationPayload payload, CancellationToken cancellationToken = default)
        {
            pushes.Enqueue(payload);
            return Task.CompletedTask;
        }

        public Task SendToBookingHostsAsync(PushNotificationPayload payload, CancellationToken cancellationToken = default)
        {
            pushes.Enqueue(payload);
            return Task.CompletedTask;
        }

        public Task SendServiceRequestUpdateAsync(Guid serviceRequestId, string statusLabel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendCheckoutReminderAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
