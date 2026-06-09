using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// US-003 (#215) branded booking site public org endpoints.
/// </summary>
public class BrandedBookingSiteIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private static readonly string[] ForbiddenJsonKeys = ["ownerId", "apiKey", "planTier", "stripeCustomerId", "stripeConnectedAccountId"];

    public BrandedBookingSiteIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC1_GetPublicOrg_ReturnsBrandingDto_WithoutAuth()
    {
        var org = await SeedOrgAsync("branded-test-org");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/orgs/{org.Slug}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertPublicJsonSafe(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(org.Slug, root.GetProperty("slug").GetString());
        Assert.Equal("Branded Test Org", root.GetProperty("displayName").GetString());
        Assert.Equal("#2563eb", root.GetProperty("themeColor").GetString());
        Assert.False(root.TryGetProperty("planTier", out _));
    }

    [Fact]
    public async Task AC1_GetPublicOrg_Returns404_ForUnknownSlug()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/orgs/does-not-exist-slug");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC2_GetOrgProperties_ReturnsOnlyOrgListings()
    {
        var orgA = await SeedOrgAsync($"org-a-{Guid.NewGuid():N}");
        var orgB = await SeedOrgAsync($"org-b-{Guid.NewGuid():N}");
        var propertyA = await SeedPropertyForOrgAsync(orgA, "Org A Villa");
        await SeedPropertyForOrgAsync(orgB, "Org B Villa");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/orgs/{orgA.Slug}/properties");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertPublicJsonSafe(json);

        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Single(ids);
        Assert.Contains(propertyA.Id, ids);
    }

    [Fact]
    public async Task AC3_GetOrgProperty_Returns404_WhenPropertyBelongsToOtherOrg()
    {
        var orgA = await SeedOrgAsync($"org-a-{Guid.NewGuid():N}");
        var orgB = await SeedOrgAsync($"org-b-{Guid.NewGuid():N}");
        var propertyB = await SeedPropertyForOrgAsync(orgB, "Other Org Property");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/orgs/{orgA.Slug}/properties/{propertyB.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC3_GetOrgProperty_ReturnsDetail_ForMatchingOrg()
    {
        var org = await SeedOrgAsync($"org-detail-{Guid.NewGuid():N}");
        var property = await SeedPropertyForOrgAsync(org, "Detail Villa", houseRules: "Check-in after 15:00");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/orgs/{org.Slug}/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertPublicJsonSafe(json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Check-in after 15:00", doc.RootElement.GetProperty("houseRules").GetString());
    }

    private static void AssertPublicJsonSafe(string json)
    {
        var lower = json.ToLowerInvariant();
        foreach (var key in ForbiddenJsonKeys)
            Assert.DoesNotContain(key.ToLowerInvariant(), lower);
    }

    private async Task<Org> SeedOrgAsync(string slug, bool isActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new Org
        {
            Name = "Branded Test Org",
            Slug = slug,
            DisplayName = "Branded Test Org",
            LogoUrl = "https://cdn.example.com/logo.png",
            ThemeColor = "#2563eb",
            ContactEmail = "contact@branded.example",
            PlanTier = PlanTier.Starter,
            IsActive = isActive,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private async Task<Property> SeedPropertyForOrgAsync(Org org, string name, string houseRules = "")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ownerId = $"auth0|owner-{Guid.NewGuid():N}";
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = name,
            Description = "Branded listing description",
            Address = $"Via Branded {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT-12345-0123456789",
            HouseRules = houseRules,
            IsActive = true,
            PhotoUrls = ["https://cdn.example.com/branded.jpg"],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }
}
