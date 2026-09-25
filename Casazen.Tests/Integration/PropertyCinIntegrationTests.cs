using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CIN over HTTP (A5-05, R-02): a real CIN is accepted in any spacing/case and stored normalized; the old invented
/// format is rejected with a localized field error.
/// </summary>
public class PropertyCinIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PropertyCinIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task UpdateCin_RealCinWithHyphensAndLowerCase_Returns204AndStoresNormalizedCin()
    {
        var owner = $"auth0|cin-valid-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}/cin", new { cinCode = "it-058091-c2-7g5ffzdz" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("IT058091C27G5FFZDZ", await ReadStoredCinAsync(property.Id));

        var detail = await ReadJsonAsync(await client.GetAsync($"/api/properties/{property.Id}/detail"));
        Assert.Equal("IT058091C27G5FFZDZ", detail.GetProperty("cinCode").GetString());
        Assert.Equal("Valid", detail.GetProperty("cinStatus").GetString());
    }

    [Fact]
    public async Task GetCinCompliance_NoDeadlineConfigured_ReturnsStatusNoneWithoutDateOrDays()
    {
        // CO-20: no Cin:ExposureDeadline by default (the date was not found in official sources, RS-2).
        var owner = $"auth0|cin-summary-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync("/api/properties/cin-compliance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await ReadJsonAsync(response)).GetProperty("summary");
        Assert.Equal("none", summary.GetProperty("deadlineStatus").GetString());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("daysUntilDeadline").ValueKind);
        Assert.True(summary.GetProperty("hasNonCompliant").GetBoolean());
    }

    [Fact]
    public async Task UpdateCin_OldInventedFormat_Returns400WithLocalizedFieldError()
    {
        var owner = $"auth0|cin-legacy-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}/cin", new { cinCode = "IT-12345-1234567890" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        var message = problem.GetProperty("errors").GetProperty("CinCode")[0].GetString();
        Assert.StartsWith("Il CIN non è nel formato ufficiale", message);
        Assert.NotEqual("IT-12345-1234567890", await ReadStoredCinAsync(property.Id));
    }

    [Fact]
    public async Task UpdateCin_InvalidFormatWithEnglishAcceptLanguage_ReturnsEnglishMessage()
    {
        var owner = $"auth0|cin-legacy-en-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}/cin", new { cinCode = "015146-CNI-01894" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = (await ReadJsonAsync(response)).GetProperty("errors").GetProperty("CinCode")[0].GetString();
        Assert.StartsWith("The CIN is not in the official format", message);
    }

    [Fact]
    public async Task UpdateProperty_CinWithSpaces_StoresNormalizedCin()
    {
        var owner = $"auth0|cin-update-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new
        {
            name = "Casa CIN",
            description = "Integration test property",
            address = $"Via CIN {Guid.NewGuid():N}",
            city = "Firenze",
            postalCode = "50100",
            bedrooms = 2,
            bathrooms = 1,
            maxGuests = 4,
            nightlyRate = 100m,
            cinCode = "IT 048017 B4 2742QNBZ",
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("IT048017B42742QNBZ", await ReadStoredCinAsync(property.Id));
    }

    private async Task<string?> ReadStoredCinAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => p.CinCode)
            .SingleAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
