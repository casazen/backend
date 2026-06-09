using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// US-001 (#212) public booking read-model over HTTP. Verifies whitelist DTOs, anti-enumeration,
/// inactive filtering, result cap, and AC6 JSON regression (no ownerId / apiKey / guest PII keys).
/// </summary>
public class PublicBookingReadModelIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private static readonly string[] ForbiddenJsonKeys = ["ownerId", "apiKey", "email", "phoneNumber", "documentNumber"];

    public PublicBookingReadModelIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC2_Search_ReturnsPublicPropertyDto_WithoutAuth()
    {
        var city = $"SearchCity-{Guid.NewGuid():N}";
        var active = await SeedPropertyAsync(isActive: true, city: city, cinCode: "IT-12345-0123456789");
        await SeedPropertyAsync(isActive: false, city: city);

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/properties/search?city={Uri.EscapeDataString(city)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertPublicJsonSafe(json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateArray());

        var first = doc.RootElement[0];
        Assert.Equal(active.Id, first.GetProperty("id").GetGuid());
        Assert.Equal("Public Search Villa", first.GetProperty("name").GetString());
        Assert.Equal("Valid", first.GetProperty("cinStatus").GetString());
        Assert.False(first.TryGetProperty("ownerId", out _));
        Assert.False(first.TryGetProperty("address", out _));
    }

    [Fact]
    public async Task AC6_Search_SerializedBody_NeverContainsForbiddenKeys()
    {
        var city = $"SafeCity-{Guid.NewGuid():N}";
        await SeedPropertyAsync(isActive: true, city: city);

        var client = _factory.CreateClient();
        var json = await client.GetAsync($"/api/properties/search?city={Uri.EscapeDataString(city)}")
            .ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap();

        AssertPublicJsonSafe(json);
    }

    [Fact]
    public async Task AC3_Search_ExcludesInactiveProperties()
    {
        var city = $"ActiveOnly-{Guid.NewGuid():N}";
        var active = await SeedPropertyAsync(isActive: true, name: "Active Listing", city: city);
        var inactiveId = (await SeedPropertyAsync(isActive: false, name: "Draft Listing", city: city)).Id;

        var client = _factory.CreateClient();
        var json = await client.GetAsync($"/api/properties/search?city={Uri.EscapeDataString(city)}")
            .ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap();

        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(active.Id, ids);
        Assert.DoesNotContain(inactiveId, ids);
    }

    [Fact]
    public async Task AC4_GetPublic_ReturnsDetailDto_ForActiveProperty()
    {
        var policyId = await SeedCancellationPolicyAsync("Flexible", "Full refund up to 7 days before check-in.");
        var property = await SeedPropertyAsync(isActive: true, cancellationPolicyId: policyId, houseRules: "No smoking.");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/properties/{property.Id}/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        AssertPublicJsonSafe(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("No smoking.", root.GetProperty("houseRules").GetString());
        Assert.Equal("Full refund up to 7 days before check-in.", root.GetProperty("cancellationPolicySummary").GetString());
        Assert.Equal("EUR", root.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("minNights").ValueKind);
    }

    [Fact]
    public async Task AC4_GetPublic_Returns404_ForInactiveProperty()
    {
        var inactive = await SeedPropertyAsync(isActive: false);

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/properties/{inactive.Id}/public");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC4_GetPublic_Returns404_ForMissingProperty()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/properties/{Guid.NewGuid()}/public");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC6_GetPublic_SerializedBody_NeverContainsForbiddenKeys()
    {
        var property = await SeedPropertyAsync(isActive: true);

        var client = _factory.CreateClient();
        var json = await client.GetAsync($"/api/properties/{property.Id}/public")
            .ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap();

        AssertPublicJsonSafe(json);
    }

    [Fact]
    public async Task AC7_Search_CapsResultsAt50()
    {
        for (var i = 0; i < 55; i++)
            await SeedPropertyAsync(isActive: true, name: $"Bulk Property {i:D2}", city: "BulkCity");

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/properties/search?city=BulkCity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetArrayLength() <= 50);
    }

    private static void AssertPublicJsonSafe(string json)
    {
        var lower = json.ToLowerInvariant();
        foreach (var key in ForbiddenJsonKeys)
            Assert.DoesNotContain(key.ToLowerInvariant(), lower);
    }

    private async Task<Property> SeedPropertyAsync(
        bool isActive = true,
        string name = "Public Search Villa",
        string city = "Rome",
        string? cinCode = "IT-12345-0123456789",
        string houseRules = "",
        Guid? cancellationPolicyId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ownerId = $"auth0|public-{Guid.NewGuid():N}";
        var org = new Org
        {
            Name = $"Org {ownerId}",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Public Test Org",
            ContactEmail = "public@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = name,
            Description = "Guest-facing description",
            Address = $"Via Public {Guid.NewGuid():N}",
            City = city,
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CleaningFee = 40m,
            DamageDeposit = 200m,
            CinCode = cinCode,
            HouseRules = houseRules,
            CancellationPolicyId = cancellationPolicyId,
            IsActive = isActive,
            PhotoUrls = ["https://cdn.example.com/photo.jpg"],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task<Guid> SeedCancellationPolicyAsync(string name, string description)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var policy = new CancellationPolicy
        {
            Name = name,
            Description = description,
            FullRefundHours = 168,
            PartialRefundPercent = 50m,
            PartialRefundHours = 72,
        };
        db.CancellationPolicies.Add(policy);
        await db.SaveChangesAsync();
        return policy.Id;
    }
}
