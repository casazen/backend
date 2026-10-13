using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for PricingAdapterController: "Suggerimenti stagionali" (D4, PC-15) over the real pipeline
/// (authentication, tenant filter, validation, database of the factory: PostgreSQL when configured).
/// </summary>
public class PricingAdapterIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PricingAdapterIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static void AssertNoApiKeyInBody(string body)
    {
        Assert.DoesNotContain("apikey", body, StringComparison.OrdinalIgnoreCase);
    }

    private static object ConfigRequest(bool enabled = true, string frequency = "daily") => new
    {
        isEnabled = enabled,
        adaptationFrequency = frequency,
        includeSeasonality = true,
        includePublicHolidays = true,
    };

    private async Task<int> SuggestionRowsAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SeasonalPriceSuggestions.IgnoreQueryFilters().CountAsync(s => s.PropertyId == propertyId);
    }

    [Fact]
    public async Task SaveConfig_Enabled_ReturnsConfigAndComputesTheSuggestions()
    {
        var property = await _factory.SeedPropertyAsync(nightlyRate: 180m);
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("daily", doc.RootElement.GetProperty("adaptationFrequency").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("nextRunOn").ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("nextScheduledRunAt", out _));
        Assert.Equal(90, await SuggestionRowsAsync(property.Id));
    }

    [Fact]
    public async Task GetConfig_NeverSaved_ReturnsTheExampleRuleDisabled()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/pricing-adapter/config/{property.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(new[] { 6, 7, 8 }, root.GetProperty("highSeasonMonths").EnumerateArray().Select(m => m.GetInt32()).ToArray());
        Assert.Equal(1.3m, root.GetProperty("highSeasonMultiplier").GetDecimal());
        Assert.Equal(1.5m, root.GetProperty("holidayMultiplier").GetDecimal());
        Assert.False(root.TryGetProperty("aiConfidence", out _));
    }

    [Fact]
    public async Task DeleteConfig_ThenGet_ReturnsDisabledAndRemovesTheSuggestions()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var deleteResponse = await client.DeleteAsync($"/api/pricing-adapter/config/{property.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        var getResponse = await client.GetAsync($"/api/pricing-adapter/config/{property.Id}");
        Assert.False(JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(0, await SuggestionRowsAsync(property.Id));
    }

    [Fact]
    public async Task GetSuggestions_PropertyAt180_ReturnsNinetyDaysOnTheRealBaseWithTheRuleApplied()
    {
        var property = await _factory.SeedPropertyAsync(nightlyRate: 180m);
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var response = await client.GetAsync($"/api/pricing-adapter/suggestions/{property.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);
        Assert.DoesNotContain("confidence", body, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(180m, doc.RootElement.GetProperty("currentBasePrice").GetDecimal());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(90, items.Count);
        Assert.All(items, i => Assert.Equal(180m, i.GetProperty("basePrice").GetDecimal()));
        Assert.All(items, i =>
        {
            var expected = i.GetProperty("rule").GetString() switch
            {
                "HighSeason" => 234m,
                "LowSeason" => 144m,
                "Holiday" => 270m,
                _ => 180m,
            };
            Assert.Equal(expected, i.GetProperty("suggestedPrice").GetDecimal());
        });
    }

    [Fact]
    public async Task Recalculate_TwiceTheSameDay_KeepsOneRowPerDate()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var first = await client.PostAsync($"/api/pricing-adapter/recalculate/{property.Id}", null);
        var second = await client.PostAsync($"/api/pricing-adapter/recalculate/{property.Id}", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("Computed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(90, doc.RootElement.GetProperty("days").GetInt32());
        Assert.Equal(90, await SuggestionRowsAsync(property.Id));
    }

    [Fact]
    public async Task Recalculate_NotEnabled_Returns422WithCode()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync($"/api/pricing-adapter/recalculate/{property.Id}", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(PricingAdapterController.SuggestionsNotEnabledCode, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SaveConfig_InvalidRules_Returns400()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        var overlap = await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", new
        {
            isEnabled = true,
            adaptationFrequency = "daily",
            highSeasonMonths = new[] { 7, 8 },
            lowSeasonMonths = new[] { 8 },
        });
        var month13 = await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", new
        {
            isEnabled = true,
            adaptationFrequency = "daily",
            highSeasonMonths = new[] { 13 },
        });
        var multiplier = await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", new
        {
            isEnabled = true,
            adaptationFrequency = "daily",
            holidayMultiplier = 50,
        });
        var frequency = await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest(frequency: "hourly"));

        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, month13.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, multiplier.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, frequency.StatusCode);
    }

    [Theory]
    [InlineData("/api/pricing-adapter/config/{0}", "POST")]
    [InlineData("/api/pricing-adapter/config/{0}", "GET")]
    [InlineData("/api/pricing-adapter/config/{0}", "DELETE")]
    [InlineData("/api/pricing-adapter/suggestions/{0}", "GET")]
    [InlineData("/api/pricing-adapter/recalculate/{0}", "POST")]
    public async Task Endpoints_WithoutJwt_ReturnUnauthorized(string routeTemplate, string method)
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateClient();
        var route = string.Format(routeTemplate, property.Id);

        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST" && route.Contains("/config/"))
            request.Content = JsonContent.Create(ConfigRequest());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pricing-adapter/history/{0}")]
    [InlineData("/api/pricing-adapter/preview/{0}")]
    public async Task RemovedEndpoints_InventedHistoryAndPreview_AreGone(string routeTemplate)
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync(string.Format(routeTemplate, property.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossOrgUser_OnAllEndpoints_ReturnsNotFound()
    {
        // The EF tenant filter scopes property reads to the caller's org: a host of another org cannot see the
        // property, so every property-scoped endpoint returns 404 (never 403, which would leak its existence).
        var property = await _factory.SeedPropertyAsync(ownerId: TestAuthHandler.DefaultUserId);
        var otherHost = $"auth0|other-host-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherHost);
        var otherClient = _factory.CreateAuthenticatedClient(userId: otherHost, roles: "PropertyOwner");
        var propertyId = property.Id;

        var save = await otherClient.PostAsJsonAsync($"/api/pricing-adapter/config/{propertyId}", ConfigRequest());
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);

        var ownerClient = _factory.CreateAuthenticatedClient();
        await ownerClient.PostAsJsonAsync($"/api/pricing-adapter/config/{propertyId}", ConfigRequest());

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/pricing-adapter/config/{propertyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/pricing-adapter/config/{propertyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/pricing-adapter/suggestions/{propertyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync($"/api/pricing-adapter/recalculate/{propertyId}", null)).StatusCode);
        Assert.Equal(90, await SuggestionRowsAsync(propertyId));
    }
}
