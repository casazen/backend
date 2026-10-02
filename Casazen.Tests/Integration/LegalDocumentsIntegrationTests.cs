using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LEGAL-TEXTS: the public endpoints serve the drafts of the Terms of Service, Privacy notice and DPA once their
/// version is configured, with the controller's data filled in; without that data they stay "in preparation"
/// (fail-closed) and the health check <c>legal</c> says so. The web pages and the onboarding read these endpoints.
/// </summary>
public class LegalDocumentsIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public LegalDocumentsIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("tos", "it")]
    [InlineData("tos", "en")]
    [InlineData("privacy", "it")]
    [InlineData("privacy", "en")]
    [InlineData("dpa", "it")]
    [InlineData("dpa", "en")]
    public async Task GetDocument_DraftActivatedWithControllerData_IsAvailableInTheRequestedLanguage(string key, string language)
    {
        await using var factory = FactoryWith(ActivatedAndComplete());
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>($"/api/legal/{key}?lang={language}");

        Assert.Equal(LegalTextFixtures.DraftVersion, document.GetProperty("version").GetString());
        Assert.True(document.GetProperty("available").GetBoolean());
        Assert.Equal(language, document.GetProperty("contentLanguage").GetString());
        var html = document.GetProperty("contentHtml").GetString()!;
        Assert.Contains("Test Rentals S.r.l.", html);
        Assert.Contains("https://casazen-app.vercel.app/legale/", html);
        Assert.DoesNotContain("{{", html);
    }

    [Theory]
    [InlineData("tos")]
    [InlineData("privacy")]
    [InlineData("dpa")]
    public async Task GetDocument_DraftActivatedWithoutControllerData_StaysInPreparation(string key)
    {
        var settings = ActivatedAndComplete();
        foreach (var name in new[] { "Name", "Address", "VatId", "Pec", "PrivacyEmail" })
            settings.Remove($"Legal:Controller:{name}");
        await using var factory = FactoryWith(settings);
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>($"/api/legal/{key}?lang=it");

        Assert.Equal(LegalTextFixtures.DraftVersion, document.GetProperty("version").GetString());
        Assert.False(document.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("contentHtml").ValueKind);
    }

    [Fact]
    public async Task Ready_DraftActivatedWithControllerData_LegalCheckIsHealthy()
    {
        await using var factory = FactoryWith(ActivatedAndComplete());
        using var client = factory.CreateClient();

        Assert.Equal("healthy", await LegalCheckAsync(client));
    }

    [Fact]
    public async Task Ready_DraftActivatedWithoutControllerData_LegalCheckIsDegradedAndRevealsNoVariable()
    {
        var settings = ActivatedAndComplete();
        settings.Remove("Legal:Controller:Name");
        await using var factory = FactoryWith(settings);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("degraded", await LegalCheckAsync(client));
        // Anonymous callers see names and statuses only (FD-12): the variable to set is for platform admins.
        Assert.DoesNotContain("Legal__Controller__Name", body);
    }

    private WebApplicationFactory<Program> FactoryWith(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    /// <summary>The drafts' versions and every value they need; the public site URL is the factory's own.</summary>
    private static Dictionary<string, string?> ActivatedAndComplete()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("App:PublicSiteBaseUrl");
        return settings;
    }

    private static async Task<string?> LegalCheckAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/health/ready");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "legal")
            .GetProperty("status").GetString();
    }
}
