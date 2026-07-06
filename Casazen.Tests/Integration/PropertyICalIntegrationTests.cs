using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    }

    [Fact]
    public async Task PublicExport_UnknownToken_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/ical/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string OwnerId, Guid PropertyId, Guid ExportToken)> SeedFeedWithBlockAsync()
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
