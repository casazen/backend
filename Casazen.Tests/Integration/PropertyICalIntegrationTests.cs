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

    [Fact]
    public async Task PublicExport_ReturnsCalendarWithoutPii()
    {
        var (ownerId, propertyId, exportToken) = await SeedFeedWithBlockAsync();

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/ical/{exportToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/calendar", response.Content.Headers.ContentType?.MediaType ?? string.Empty);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BEGIN:VEVENT", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reserved", body);
        Assert.DoesNotContain("@", body);
    }

    [Fact]
    public async Task GetStatus_AsOwner_ReturnsBlockCount()
    {
        var (ownerId, propertyId, _) = await SeedFeedWithBlockAsync();

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync($"/api/properties/{propertyId}/ical/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("blockCount").GetInt32() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("exportUrl").GetString()));
    }

    [Fact]
    public async Task Calendar_IncludesIcalBlockItems()
    {
        var (ownerId, propertyId, _) = await SeedFeedWithBlockAsync();

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync(
            $"/api/bookings/calendar?propertyId={propertyId}&startDate=2026-07-01&endDate=2026-07-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("type").GetString() == "ical-block");
    }

    [Fact]
    public async Task ImportUrl_InvalidScheme_Returns400()
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/import-url",
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
    public async Task ImportUrl_PrivateLoopbackOrMetadataAddress_Returns400WithCodeAndSavesNothing(string url)
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/import-url",
            new { importUrl = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalErrorCodes.InvalidUrl, body.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PropertyICalFeeds.AnyAsync(f => f.PropertyId == property.Id && f.ImportUrl != null));
        VerifyFirstSyncQueued(property.Id, Times.Never());
    }

    // FD-16 (A2-21): saving the URL answers at once and leaves the download to a Hangfire job.
    [Fact]
    public async Task ImportUrl_PublicHttpsUrl_Returns202SyncingAndQueuesFirstSync()
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/import-url",
            new { importUrl = "https://www.airbnb.it/calendar/ical/12345.ics?s=secret" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Syncing", body.GetProperty("lastImportStatus").GetString());
        Assert.Equal("https://www.airbnb.it/calendar/ical/12345.ics?s=secret", body.GetProperty("importUrl").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastErrorCode").ValueKind);
        VerifyFirstSyncQueued(property.Id, Times.Once());
    }

    // TN-3 schema on the actions touched by FD-16: a host of another org does not see the property.
    [Fact]
    public async Task ImportUrl_HostOfAnotherOrg_Returns404AndQueuesNothing()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|host-{Guid.NewGuid():N}");
        var otherHost = $"auth0|other-host-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherHost);

        using var client = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");
        var save = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/ical/import-url",
            new { importUrl = "https://www.airbnb.it/calendar/ical/1.ics" });
        var status = await client.GetAsync($"/api/properties/{property.Id}/ical/status");

        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        VerifyFirstSyncQueued(property.Id, Times.Never());
    }

    // FD-16 (A2-21): a stored error is shown as a code plus localized text, never as the exception message.
    [Fact]
    public async Task GetStatus_WithStoredErrorCode_ReturnsCodeAndLocalizedMessage()
    {
        var (ownerId, propertyId, _) = await SeedFeedWithBlockAsync(lastError: ICalErrorCodes.TooLarge);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var response = await client.GetAsync($"/api/properties/{propertyId}/ical/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalErrorCodes.TooLarge, body.GetProperty("lastErrorCode").GetString());
        Assert.Equal("The calendar is too large to be imported.", body.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task GetStatus_WithLegacyExceptionMessage_ReturnsGenericCodeWithoutTheMessage()
    {
        const string leakedMessage = "Connection refused (10.0.0.5:443)";
        var (ownerId, propertyId, _) = await SeedFeedWithBlockAsync(lastError: leakedMessage);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync($"/api/properties/{propertyId}/ical/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("10.0.0.5", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(ICalErrorCodes.SyncFailed, body.GetProperty("lastErrorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("lastError").GetString()));
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

    private void VerifyFirstSyncQueued(Guid propertyId, Times times) =>
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(job => job.Type == typeof(PropertyICalSyncJob)
                    && job.Method.Name == nameof(PropertyICalSyncJob.SyncFeedAsync)
                    && (Guid)job.Args[0] == propertyId),
                It.IsAny<IState>()),
            times);

    private async Task<(string OwnerId, Guid PropertyId, Guid ExportToken)> SeedFeedWithBlockAsync(string? lastError = null)
    {
        var ownerId = $"auth0|host-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);
        var exportToken = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.PropertyICalFeeds.Add(new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ImportUrl = "https://example.com/cal.ics",
            ExportToken = exportToken,
            LastError = lastError,
            LastImportStatus = lastError is null ? null : PropertyICalImportStatus.Failure,
        });
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Source = CalendarBlockSource.ICalImport,
            ExternalUid = "seed-block-1",
            StartUtc = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            Summary = "Reserved",
        });
        await db.SaveChangesAsync();

        return (ownerId, property.Id, exportToken);
    }
}
