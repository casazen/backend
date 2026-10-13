using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Resources;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public class PropertyICalIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PropertyICalIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    // PC-12 (A2-22): the imported block (SUMMARY "Reserved" from the OTA) is not sent back, the manual block is exported
    // as an all-day event with the neutral localized SUMMARY, never with the text the host typed.
    [Theory]
    [InlineData(null, "Occupato")]
    [InlineData("en", "Booked")]
    public async Task PublicExport_ImportedAndManualBlocks_ExportsOnlyTheManualBlockWithANeutralSummary(
        string? acceptLanguage, string expectedSummary)
    {
        var (_, propertyId, exportToken, _) = await SeedFeedWithBlockAsync();
        var manualBlockId = await SeedManualBlockAsync(propertyId, "Mario Rossi mario.rossi@example.com");

        using var client = _factory.CreateClient();
        if (acceptLanguage is not null)
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);
        var response = await client.GetAsync($"/api/public/ical/{exportToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/calendar", response.Content.Headers.ContentType?.MediaType ?? string.Empty);
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split("\r\n");
        Assert.Contains("BEGIN:VCALENDAR", lines);
        Assert.Single(lines, l => l == "BEGIN:VEVENT");
        Assert.Contains($"UID:block-{manualBlockId}", lines);
        Assert.Contains($"SUMMARY:{expectedSummary}", lines);
        Assert.Contains("DTSTART;VALUE=DATE:20260801", lines);
        Assert.Contains("DTEND;VALUE=DATE:20260803", lines);
        Assert.DoesNotContain("Reserved", body);
        Assert.DoesNotContain("seed-block-1", body);
        Assert.DoesNotContain("Mario", body);
        Assert.DoesNotContain("@", body);
    }

    [Fact]
    public async Task GetStatus_AsOwner_ReturnsExportUrlBlockCountAndFeeds()
    {
        var (ownerId, propertyId, exportToken, feedId) = await SeedFeedWithBlockAsync();

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync($"/api/properties/{propertyId}/ical/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("blockCount").GetInt32());
        Assert.EndsWith($"/api/public/ical/{exportToken}", body.GetProperty("exportUrl").GetString());
        var feed = Assert.Single(body.GetProperty("feeds").EnumerateArray());
        Assert.Equal(feedId, feed.GetProperty("id").GetGuid());
        Assert.Equal(1, feed.GetProperty("blockCount").GetInt32());
        Assert.False(body.TryGetProperty("importUrl", out _));
    }

    [Fact]
    public async Task Calendar_IncludesIcalBlockItems()
    {
        var (ownerId, propertyId, _, _) = await SeedFeedWithBlockAsync();

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync(
            $"/api/bookings/calendar?propertyId={propertyId}&startDate=2026-07-01&endDate=2026-07-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("type").GetString() == "ical-block");
    }

    [Fact]
    public async Task AddFeed_InvalidScheme_Returns400()
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/feeds",
            new { importUrl = "http://insecure.example.com/cal.ics" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalErrorCodes.InvalidUrl, body.GetProperty("code").GetString());
    }

    // FD-16 (A2-21, A9-32): no internal destination can be saved as an import URL.
    [Theory]
    [InlineData("https://127.0.0.1/cal.ics")]
    [InlineData("https://10.0.0.8/cal.ics")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("https://postgres.railway.internal/cal.ics")]
    [InlineData("https://localhost/cal.ics")]
    public async Task AddFeed_PrivateLoopbackOrMetadataAddress_Returns400WithCodeAndSavesNothing(string url)
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/feeds",
            new { importUrl = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalErrorCodes.InvalidUrl, body.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PropertyICalFeeds.AnyAsync(f => f.PropertyId == property.Id));
    }

    [Fact]
    public async Task AddFeed_LabelWithLineBreak_Returns400InvalidLabel()
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/feeds",
            new { importUrl = "https://www.airbnb.it/calendar/ical/1.ics", label = "Airbnb\nBooking" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalFeedErrorCodes.InvalidLabel, body.GetProperty("code").GetString());
    }

    // FD-16 (A2-21), PC-11: adding a feed answers at once, masked, and leaves the download to a Hangfire job.
    [Fact]
    public async Task AddFeed_PublicHttpsUrl_Returns202SyncingMaskedAndQueuesItsSync()
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/feeds",
            new { importUrl = "https://www.airbnb.it/calendar/ical/12345.ics?s=secret9f3a" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Syncing", body.GetProperty("lastImportStatus").GetString());
        Assert.Equal("Airbnb", body.GetProperty("channel").GetString());
        Assert.Equal("www.airbnb.it/…9f3a", body.GetProperty("maskedImportUrl").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastErrorCode").ValueKind);
        VerifySyncQueued(body.GetProperty("id").GetGuid(), Times.Once());
    }

    // FD-16 (A2-21): a stored error is shown as a code plus localized text, never as the exception message.
    [Fact]
    public async Task GetFeeds_WithStoredErrorCode_ReturnsCodeAndLocalizedMessagePerFeed()
    {
        var (ownerId, propertyId, _, _) = await SeedFeedWithBlockAsync(lastError: ICalErrorCodes.TooLarge);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var response = await client.GetAsync($"/api/properties/{propertyId}/ical/feeds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var feed = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("Failure", feed.GetProperty("lastImportStatus").GetString());
        Assert.Equal(ICalErrorCodes.TooLarge, feed.GetProperty("lastErrorCode").GetString());
        Assert.Equal("The calendar is too large to be imported.", feed.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task GetFeeds_WithLegacyExceptionMessage_ReturnsGenericCodeWithoutTheMessage()
    {
        const string leakedMessage = "Connection refused (10.0.0.5:443)";
        var (ownerId, propertyId, _, _) = await SeedFeedWithBlockAsync(lastError: leakedMessage);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync($"/api/properties/{propertyId}/ical/feeds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("10.0.0.5", raw);
        var feed = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray());
        Assert.Equal(ICalErrorCodes.SyncFailed, feed.GetProperty("lastErrorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(feed.GetProperty("lastError").GetString()));
    }

    // "Sync now": 202 Syncing and one job for that feed; a second click while it is pending queues nothing.
    [Fact]
    public async Task SyncFeed_Twice_QueuesOneJobForThatFeed()
    {
        var (ownerId, propertyId, _, feedId) = await SeedFeedWithBlockAsync(lastError: ICalErrorCodes.Unreachable);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var first = await client.PostAsync($"/api/properties/{propertyId}/ical/feeds/{feedId}/sync", null);
        var second = await client.PostAsync($"/api/properties/{propertyId}/ical/feeds/{feedId}/sync", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Syncing", body.GetProperty("lastImportStatus").GetString());
        Assert.Equal(1, body.GetProperty("blockCount").GetInt32());
        VerifySyncQueued(feedId, Times.Once());
    }

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void FeedErrorCodes_EveryCode_HasLocalizedMessage(string culture)
    {
        var localizer = _factory.Services.GetRequiredService<IStringLocalizer<SharedResources>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            foreach (var key in new[]
                     {
                         ICalFeedErrorCodes.NotFoundMessageKey, ICalFeedErrorCodes.LimitReachedMessageKey,
                         ICalFeedErrorCodes.DuplicateMessageKey, ICalFeedErrorCodes.InvalidLabelMessageKey,
                         ICalFeedErrorCodes.SupplierNoFeedMessageKey,
                     })
            {
                Assert.False(localizer[key].ResourceNotFound, $"No {culture} message for {key}");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void ErrorCodes_EveryCode_HasLocalizedMessage(string culture)
    {
        var localizer = _factory.Services.GetRequiredService<IStringLocalizer<SharedResources>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Assert.All(ICalErrorCodes.All, code =>
            {
                var message = localizer[ICalErrorCodes.MessageKey(code)];
                Assert.False(message.ResourceNotFound, $"No {culture} message for {code}");
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task PublicExport_UnknownToken_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/ical/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private void VerifySyncQueued(Guid feedId, Times times) =>
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(job => job.Type == typeof(PropertyICalSyncJob)
                    && job.Method.Name == nameof(PropertyICalSyncJob.SyncFeedAsync)
                    && (Guid)job.Args[0] == feedId),
                It.IsAny<IState>()),
            times);

    private async Task<Guid> SeedManualBlockAsync(Guid propertyId, string summary)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgId = await db.Properties.IgnoreQueryFilters().Where(p => p.Id == propertyId).Select(p => p.OrgId).SingleAsync();
        var block = new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
            Source = CalendarBlockSource.Manual,
            StartUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
            Summary = summary,
        };
        db.CalendarBlocks.Add(block);
        await db.SaveChangesAsync();
        return block.Id;
    }

    private async Task<(string OwnerId, Guid PropertyId, Guid ExportToken, Guid FeedId)> SeedFeedWithBlockAsync(string? lastError = null)
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);
        var exportToken = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feed = new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ImportUrl = "https://example.com/cal.ics",
            LastError = lastError,
            LastImportStatus = lastError is null ? null : PropertyICalImportStatus.Failure,
        };
        db.PropertyICalFeeds.Add(feed);
        db.PropertyICalExports.Add(new PropertyICalExport
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ExportToken = exportToken,
        });
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            FeedId = feed.Id,
            Source = CalendarBlockSource.ICalImport,
            ExternalUid = "seed-block-1",
            StartUtc = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            Summary = "Reserved",
        });
        await db.SaveChangesAsync();

        return (ownerId, property.Id, exportToken, feed.Id);
    }
}
