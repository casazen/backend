using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SE-04 (A8-10, A8-11, A8-18; #300 AC2, AC3, AC9) through the whole pipeline: the featured properties of a comune, the
/// anonymous funnel events with no personal data, the admin report and the retention.
/// </summary>
public class SeoFunnelIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public SeoFunnelIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // ─── AC3: events ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordEvent_AnonymousCtaClick_Returns204AndStoresOnlyTheNonPersonalFields()
    {
        var before = await CountEventsAsync("013075");
        using var client = _factory.CreateClient();
        // Headers a tracker would be tempted to store: none of them may reach the row.
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.77");
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (test)");
        client.DefaultRequestHeaders.Add("Cookie", "visitor=abc123");

        var response = await client.PostAsync("/api/public/seo/events", Json(new
        {
            @event = "cta_click",
            comuneSlug = "como",
            utmSource = "seo-compliance",
            utmMedium = "cta",
            utmCampaign = "estate",
            referrerHost = "www.google.com",
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.SeoEvents.AsNoTracking().Where(e => e.ComuneCode == "013075").OrderByDescending(e => e.OccurredAt).FirstAsync();
        Assert.Equal(before + 1, await db.SeoEvents.CountAsync(e => e.ComuneCode == "013075"));
        Assert.Equal(SeoEventType.CtaClick, stored.Event);
        Assert.Equal("seo-compliance", stored.UtmSource);
        Assert.Equal("www.google.com", stored.ReferrerHost);
        var row = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("203.0.113.77", row);
        Assert.DoesNotContain("Mozilla", row);
        Assert.DoesNotContain("abc123", row);
    }

    [Fact]
    public async Task RecordEvent_UnknownComune_Returns422WithAStableCodeAndStoresNothing()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/public/seo/events", Json(new { @event = "cta_click", comuneSlug = "atlantide" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ISeoEventService.UnknownComuneCode, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RecordEvent_UnknownEvent_Returns422()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/public/seo/events", Json(new { @event = "page_view", comuneSlug = "como" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ISeoEventService.UnknownEventCode, problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("utmSource", "mario.rossi@example.com")]
    [InlineData("utmMedium", "<script>")]
    [InlineData("referrerHost", "https://www.google.com/search?q=casa")]
    [InlineData("comuneSlug", "Como!")]
    public async Task RecordEvent_ValueOutsideTheRules_Returns400AndStoresNothing(string field, string value)
    {
        var before = await CountEventsAsync("013075");
        using var client = _factory.CreateClient();
        var body = new Dictionary<string, string> { ["event"] = "cta_click", ["comuneSlug"] = "como", [field] = value };

        var response = await client.PostAsync("/api/public/seo/events", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountEventsAsync("013075"));
    }

    [Fact]
    public async Task RecordEvent_NoBody_Returns400()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/public/seo/events", Json(new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── AC9: admin report ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TopComuni_AsAdmin_ReturnsClicksAndStartsOfTheComuneWithTheRetention()
    {
        using var anonymous = _factory.CreateClient();
        // A comune of its own for this test: the class shares one database.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync("/api/public/seo/events", Json(new { @event = "cta_click", comuneSlug = "palermo" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync("/api/public/seo/events", Json(new { @event = "signup_start", comuneSlug = "palermo" }))).StatusCode);
        using var admin = _factory.CreateAuthenticatedClient($"auth0|se04-admin-{Guid.NewGuid():N}", "Admin");

        var response = await admin.GetAsync("/api/admin/seo/top-comuni?days=30&limit=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30, body.GetProperty("days").GetInt32());
        Assert.Equal(90, body.GetProperty("retentionDays").GetInt32());
        var palermo = body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("comuneCode").GetString() == "082053");
        Assert.Equal("Palermo", palermo.GetProperty("comuneName").GetString());
        Assert.True(palermo.GetProperty("ctaClicks").GetInt32() >= 3);
        Assert.True(palermo.GetProperty("signupStarts").GetInt32() >= 1);
        Assert.Equal(0, palermo.GetProperty("signups").GetInt32());
    }

    [Fact]
    public async Task TopComuni_Anonymous_Returns401()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/seo/top-comuni")).StatusCode);
    }

    [Fact]
    public async Task TopComuni_AsHost_Returns403()
    {
        var hostId = $"auth0|se04-host-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(hostId);
        using var host = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/admin/seo/top-comuni")).StatusCode);
    }

    // ─── Retention ───────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task PurgeExpired_DeletesOnlyTheEventsOlderThanTheRetention()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISeoEventService>();
        var now = DateTime.UtcNow;
        var oldEvent = new SeoEvent { Event = SeoEventType.CtaClick, ComuneCode = "070001", OccurredAt = now.AddDays(-91) };
        var edge = new SeoEvent { Event = SeoEventType.CtaClick, ComuneCode = "070001", OccurredAt = now.AddDays(-89) };
        var recent = new SeoEvent { Event = SeoEventType.SignupStart, ComuneCode = "070001", OccurredAt = now.AddHours(-1) };
        db.SeoEvents.AddRange(oldEvent, edge, recent);
        await db.SaveChangesAsync();

        var deleted = await service.PurgeExpiredAsync();

        Assert.True(deleted >= 1);
        var left = await db.SeoEvents.AsNoTracking().Where(e => e.ComuneCode == "070001").Select(e => e.Id).ToListAsync();
        Assert.DoesNotContain(oldEvent.Id, left);
        Assert.Contains(edge.Id, left);
        Assert.Contains(recent.Id, left);
    }

    // ─── AC2: featured properties ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FeaturedProperties_Comune_ListsOnlyThePublishedPropertiesOfThatComuneWithTheirBookingKeys()
    {
        var host = $"auth0|se04-featured-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(host);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var published = await SeedPropertyAsync(org, $"Villa Lago {tag}", "Como", p => p.Slug = $"villa-lago-{tag}");
        await SeedPropertyAsync(org, $"Villa in pausa {tag}", "Como", p => { p.IsPaused = true; p.PausedAt = DateTime.UtcNow; });
        await SeedPropertyAsync(org, $"Villa da attivare {tag}", "Como", p => p.ComplianceStatus = PropertyComplianceStatus.Pending);
        await SeedPropertyAsync(org, $"Villa disattivata {tag}", "Como", p => p.IsActive = false);
        await SeedPropertyAsync(org, $"Villa eliminata {tag}", "Como", p => { p.IsDeleted = true; p.DeletedAt = DateTime.UtcNow; });
        await SeedPropertyAsync(org, $"Casa a Roma {tag}", "Roma");
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/public/seo/como/featured-properties");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("como", body.GetProperty("comuneSlug").GetString());
        Assert.Equal("Como", body.GetProperty("comuneName").GetString());
        var mine = body.GetProperty("properties").EnumerateArray().Where(p => p.GetProperty("name").GetString()!.EndsWith(tag)).ToList();
        var only = Assert.Single(mine);
        Assert.Equal("Villa Lago " + tag, only.GetProperty("name").GetString());
        Assert.Equal(published, only.GetProperty("id").GetGuid());
        Assert.Equal($"villa-lago-{tag}", only.GetProperty("slug").GetString());
        Assert.Equal(org.Slug, only.GetProperty("orgSlug").GetString());
        Assert.Equal("Como", only.GetProperty("city").GetString());
        Assert.Equal("https://cdn.example.test/foto-1.jpg", only.GetProperty("photoUrl").GetString());
        // The anonymous card carries no owner, no address and no tax data.
        Assert.False(only.TryGetProperty("ownerId", out _));
        Assert.False(only.TryGetProperty("address", out _));
        Assert.False(only.TryGetProperty("cinCode", out _));
    }

    [Fact]
    public async Task FeaturedProperties_OrgDisabled_ListsNothingOfIt()
    {
        var host = $"auth0|se04-disabled-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(host);
        var tag = Guid.NewGuid().ToString("N")[..8];
        await SeedPropertyAsync(org, $"Villa Spenta {tag}", "Como");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.Orgs.IgnoreQueryFilters().FirstAsync(o => o.Id == org.Id);
            tracked.IsActive = false;
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/seo/como/featured-properties");

        Assert.DoesNotContain(body.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString()!.EndsWith(tag));
    }

    [Fact]
    public async Task FeaturedProperties_ComuneWithoutPublishedProperties_Returns200WithAnEmptyList()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/public/seo/varenna/featured-properties");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("properties").EnumerateArray());
    }

    [Theory]
    [InlineData("atlantide")]
    [InlineData("Como!")]
    public async Task FeaturedProperties_UnknownOrMalformedComune_Returns404(string comune)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/seo/{Uri.EscapeDataString(comune)}/featured-properties");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FeaturedProperties_ComuneByIstatCode_ResolvesLikeTheSlug()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/seo/013075/featured-properties");

        Assert.Equal("Como", body.GetProperty("comuneName").GetString());
    }

    [Fact]
    public async Task FeaturedProperties_ComuneIstatCodeIsTheKey_CityTextOnlyFallsBackWhenThereIsNoCode()
    {
        var host = $"auth0|se04-relink-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(host);
        var tag = Guid.NewGuid().ToString("N")[..8];
        // Chosen from the official list: found by the code even if the free-text city is spelled differently.
        await SeedPropertyAsync(org, $"Con codice {tag}", "Lago di Como", p => p.ComuneIstatCode = "013075");
        // Code of another comune wins over a city text that says Como: not listed under Como.
        await SeedPropertyAsync(org, $"Codice altrove {tag}", "Como", p => p.ComuneIstatCode = "058091");
        // No code yet: the city name is the documented fallback.
        await SeedPropertyAsync(org, $"Solo citta {tag}", "como");
        // No code and another city: not listed.
        await SeedPropertyAsync(org, $"Altra citta {tag}", "Roma");
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/seo/como/featured-properties");

        var names = body.GetProperty("properties").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!).Where(n => n.EndsWith(tag)).OrderBy(n => n).ToList();
        Assert.Equal([$"Con codice {tag}", $"Solo citta {tag}"], names);
    }

    private async Task<int> CountEventsAsync(string comuneCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SeoEvents.CountAsync(e => e.ComuneCode == comuneCode);
    }

    private async Task<Guid> SeedPropertyAsync(OrgEntity org, string name, string city, Action<Property>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = "owner-se04",
            OrgId = org.Id,
            Name = name,
            Description = "Casa di prova",
            Address = $"Via Prova {Guid.NewGuid():N}",
            City = city,
            PostalCode = "22100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            PhotoUrls = ["https://cdn.example.test/foto-1.jpg", "https://cdn.example.test/foto-2.jpg"],
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        change?.Invoke(property);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }
}
