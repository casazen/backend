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

/// <summary>
/// <c>GET /api/properties/search</c> (BK-20, A8-13) over HTTP: the filters of the public search, the org slug that makes a
/// result linkable to <c>/book/{orgSlug}/property/{slug}</c>, and the bounds of the filter values.
/// </summary>
public class PublicPropertySearchIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PublicPropertySearchIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Search_Result_CarriesTheOrgSlugAndTheLinkItBuildsResolves()
    {
        var city = NewCity();
        var seeded = await SeedAsync(city, "Casa del Faro");

        using var client = _factory.CreateClient();
        var results = await SearchAsync(client, $"city={city}");

        var result = Assert.Single(results);
        Assert.Equal(seeded.OrgSlug, result.GetProperty("orgSlug").GetString());
        Assert.Equal("casa-del-faro", result.GetProperty("slug").GetString());

        // /book/{orgSlug}/property/{slug}: the public API of that page finds the same property.
        var page = await client.GetAsync($"/api/public/orgs/{seeded.OrgSlug}/properties/casa-del-faro");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using var detail = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        Assert.Equal(seeded.PropertyId, detail.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Search_EveryFilter_IsAppliedByTheApi()
    {
        var city = NewCity();
        await SeedAsync(city, "Piccola", rate: 60m, bedrooms: 1, bathrooms: 1, guests: 2);
        await SeedAsync(city, "Media", rate: 120m, bedrooms: 2, bathrooms: 1, guests: 4);
        await SeedAsync(city, "Grande", rate: 300m, bedrooms: 4, bathrooms: 3, guests: 8);

        using var client = _factory.CreateClient();

        Assert.Equal(["Grande", "Media"], await NamesAsync(client, $"city={city}&minPrice=100"));
        Assert.Equal(["Media", "Piccola"], await NamesAsync(client, $"city={city}&maxPrice=150"));
        Assert.Equal(["Media"], await NamesAsync(client, $"city={city}&minPrice=100&maxPrice=150"));
        Assert.Equal(["Grande", "Media"], await NamesAsync(client, $"city={city}&bedrooms=2"));
        Assert.Equal(["Grande"], await NamesAsync(client, $"city={city}&bathrooms=2"));
        Assert.Equal(["Grande", "Media"], await NamesAsync(client, $"city={city}&guests=3"));
        Assert.Equal(["Grande"], await NamesAsync(client, $"city={city}&guests=3&bedrooms=3&bathrooms=3&minPrice=200"));
        Assert.Equal(["Grande", "Media", "Piccola"], await NamesAsync(client, $"city={city}"));
    }

    [Fact]
    public async Task Search_PropertyOfAnInactiveOrg_IsNotOffered()
    {
        var city = NewCity();
        var closed = await SeedAsync(city, "Chiusa");
        await SeedAsync(city, "Aperta");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var org = await db.Orgs.SingleAsync(o => o.Slug == closed.OrgSlug);
            org.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();

        Assert.Equal(["Aperta"], await NamesAsync(client, $"city={city}"));
    }

    [Theory]
    [InlineData("guests=0")]
    [InlineData("guests=101")]
    [InlineData("guests=abc")]
    [InlineData("bedrooms=-1")]
    [InlineData("bedrooms=51")]
    [InlineData("bathrooms=-1")]
    [InlineData("bathrooms=51")]
    [InlineData("minPrice=-1")]
    [InlineData("maxPrice=-1")]
    [InlineData("minPrice=1000001")]
    [InlineData("maxPrice=1000001")]
    public async Task Search_ValueOutOfRange_Returns400(string query)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/properties/search?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_CityTooLong_Returns400()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/properties/search?city={new string('a', 101)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OrgPropertyList_Result_CarriesTheOrgSlugToo()
    {
        var seeded = await SeedAsync(NewCity(), "Villa Verde");

        using var client = _factory.CreateClient();
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{seeded.OrgSlug}/properties");

        Assert.Equal(seeded.OrgSlug, json.EnumerateArray().Single().GetProperty("orgSlug").GetString());
    }

    private static string NewCity() => $"Citta{Guid.NewGuid():N}";

    private static async Task<List<JsonElement>> SearchAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/properties/search?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<List<string>> NamesAsync(HttpClient client, string query) =>
        (await SearchAsync(client, query)).Select(p => p.GetProperty("name").GetString()!).Order().ToList();

    private sealed record Seeded(Guid PropertyId, string OrgSlug);

    private async Task<Seeded> SeedAsync(
        string city,
        string name,
        decimal rate = 100m,
        int bedrooms = 2,
        int bathrooms = 1,
        int guests = 4)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ownerId = $"auth0|search-{Guid.NewGuid():N}";
        var org = new OrgEntity
        {
            Name = $"Org {ownerId}",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Search Test Org",
            ContactEmail = "search@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            Description = "Guest-facing description",
            Address = $"Via Search {Guid.NewGuid():N}",
            City = city,
            PostalCode = "22100",
            Latitude = 45.81m,
            Longitude = 9.08m,
            Bedrooms = bedrooms,
            Bathrooms = bathrooms,
            MaxGuests = guests,
            NightlyRate = rate,
            CleaningFee = 40m,
            DamageDeposit = 200m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            PhotoUrls = ["https://cdn.example.com/photo.jpg"],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return new Seeded(property.Id, org.Slug);
    }
}
