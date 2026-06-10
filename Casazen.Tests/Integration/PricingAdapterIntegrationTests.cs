using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for PricingAdapterController (spec AC1–AC9).
/// Uses in-memory EF and TestAuthHandler — runs in CI on Linux.
/// </summary>
public class PricingAdapterIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public PricingAdapterIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static void AssertNoApiKeyInBody(string body)
    {
        Assert.DoesNotContain("apikey", body, StringComparison.OrdinalIgnoreCase);
    }

    private static object ConfigRequest(bool enabled = true) => new
    {
        isEnabled = enabled,
        adaptationFrequency = "daily",
        includeSeasonality = true,
        includePublicHolidays = true,
    };

    [Fact]
    public async Task AC1_SaveConfig_Enabled_ReturnsConfigWithIsEnabledTrue()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/pricing-adapter/config/{property.Id}",
            ConfigRequest(enabled: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("daily", doc.RootElement.GetProperty("adaptationFrequency").GetString());
    }

    [Fact]
    public async Task AC2_GetConfig_ReturnsAllConfigFields()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var response = await client.GetAsync($"/api/pricing-adapter/config/{property.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("adaptationFrequency", out _));
        Assert.True(root.TryGetProperty("includeSeasonality", out _));
        Assert.True(root.TryGetProperty("includePublicHolidays", out _));
        Assert.True(root.TryGetProperty("nextScheduledRunAt", out var nextRun));
        Assert.False(nextRun.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    [Fact]
    public async Task AC3_DeleteConfig_ThenGet_ReturnsDisabledOrNotFound()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var deleteResponse = await client.DeleteAsync($"/api/pricing-adapter/config/{property.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/pricing-adapter/config/{property.Id}");
        Assert.True(
            getResponse.StatusCode == HttpStatusCode.NotFound ||
            (getResponse.StatusCode == HttpStatusCode.OK &&
             !JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync())
                 .RootElement.GetProperty("isEnabled").GetBoolean()));
    }

    [Fact]
    public async Task AC4_GetPreview_ReturnsExactlyNinetyItems()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var response = await client.GetAsync($"/api/pricing-adapter/preview/{property.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);

        using var doc = JsonDocument.Parse(body);
        var prices = doc.RootElement.GetProperty("prices");
        Assert.Equal(90, prices.GetArrayLength());
    }

    [Fact]
    public async Task AC5_TriggerSync_ReturnsAcceptedWithJobId()
    {
        var property = await _factory.SeedPropertyAsync();
        var client = _factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var response = await client.PostAsync($"/api/pricing-adapter/sync/{property.Id}", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);

        using var doc = JsonDocument.Parse(body);
        var jobId = doc.RootElement.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
    }

    [Fact]
    public async Task AC6_GetHistory_ReturnsPaginatedEnvelope()
    {
        var property = await _factory.SeedPropertyAsync();
        await _factory.SeedPricingHistoryAsync(property.Id, count: 5);
        var client = _factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var response = await client.GetAsync($"/api/pricing-adapter/history/{property.Id}?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        AssertNoApiKeyInBody(body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("items", out var items));
        Assert.True(root.TryGetProperty("total", out var total));
        Assert.True(root.TryGetProperty("page", out var page));
        Assert.Equal(2, items.GetArrayLength());
        Assert.True(total.GetInt32() >= 5);
        Assert.Equal(1, page.GetInt32());
    }

    [Theory]
    [InlineData("/api/pricing-adapter/config/{0}", "POST")]
    [InlineData("/api/pricing-adapter/config/{0}", "GET")]
    [InlineData("/api/pricing-adapter/config/{0}", "DELETE")]
    [InlineData("/api/pricing-adapter/preview/{0}", "GET")]
    [InlineData("/api/pricing-adapter/sync/{0}", "POST")]
    [InlineData("/api/pricing-adapter/history/{0}", "GET")]
    public async Task AC7_Endpoints_WithoutJwt_ReturnUnauthorized(string routeTemplate, string method)
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

    [Fact]
    public async Task AC8_CrossOrgUser_OnAllEndpoints_ReturnsNotFound()
    {
        // After US-004 (#202) the EF global tenant filter scopes property reads to the caller's
        // org. A user from another org (here: one with no org at all) cannot see the property,
        // so every property-scoped endpoint returns 404 — never another org's row, and never 403
        // (which would leak the property's existence). This is the tenant-isolation contract.
        var property = await _factory.SeedPropertyAsync(ownerId: TestAuthHandler.DefaultUserId);
        var otherClient = _factory.CreateAuthenticatedClient(userId: "auth0|other-user-456");
        var propertyId = property.Id;

        var save = await otherClient.PostAsJsonAsync($"/api/pricing-adapter/config/{propertyId}", ConfigRequest());
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);

        // Seed config as owner for remaining endpoints
        var ownerClient = _factory.CreateAuthenticatedClient();
        await ownerClient.PostAsJsonAsync($"/api/pricing-adapter/config/{propertyId}", ConfigRequest());

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/pricing-adapter/config/{propertyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/pricing-adapter/config/{propertyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/pricing-adapter/preview/{propertyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync($"/api/pricing-adapter/sync/{propertyId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/pricing-adapter/history/{propertyId}")).StatusCode);
    }

    [Fact]
    public async Task AC9_AllResponses_DoNotContainApiKeyField()
    {
        var property = await _factory.SeedPropertyAsync();
        await _factory.SeedPricingHistoryAsync(property.Id);
        var client = _factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync($"/api/pricing-adapter/config/{property.Id}", ConfigRequest());

        var endpoints = new[]
        {
            await client.GetAsync($"/api/pricing-adapter/config/{property.Id}"),
            await client.GetAsync($"/api/pricing-adapter/preview/{property.Id}"),
            await client.PostAsync($"/api/pricing-adapter/sync/{property.Id}", null),
            await client.GetAsync($"/api/pricing-adapter/history/{property.Id}"),
        };

        foreach (var response in endpoints)
        {
            response.EnsureSuccessStatusCode();
            AssertNoApiKeyInBody(await response.Content.ReadAsStringAsync());
        }
    }
}
