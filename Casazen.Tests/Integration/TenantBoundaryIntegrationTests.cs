using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// US-004 (#202) tenant boundary over HTTP. Exercises the real request pipeline (auth + EF global
/// tenant filter + entitlement) on the in-memory harness:
/// AC7 cross-org reads return 404/empty, AC8 plan entitlement, AC9 /me returns org, AC10 an owner
/// still sees exactly their own rows post-tenant-scoping. Each test uses a unique owner so the
/// shared class-fixture in-memory store cannot leak property counts between tests.
/// </summary>
public class TenantBoundaryIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public TenantBoundaryIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewOwner() => $"auth0|owner-{Guid.NewGuid():N}";

    private static object ValidPropertyBody() => new
    {
        name = "Nuovo Appartamento",
        address = "Via Roma 1",
        city = "Rome",
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 90m,
        cinCode = "IT-12345-0123456789",
    };

    [Fact]
    public async Task AC9_GetMe_ReturnsCallerOrg()
    {
        var owner = NewOwner();
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner);

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("orgId").ValueKind);
        var org = root.GetProperty("org");
        Assert.NotEqual(JsonValueKind.Null, org.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(org.GetProperty("slug").GetString()));
        Assert.Equal("Starter", org.GetProperty("planTier").GetString());
    }

    [Fact]
    public async Task AC8_GetEntitlement_ReturnsTierLimitsAndUsage()
    {
        var owner = NewOwner();
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/orgs/me/entitlement");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Starter", root.GetProperty("planTier").GetString());
        Assert.Equal(3, root.GetProperty("limits").GetProperty("maxProperties").GetInt32());
        Assert.Equal(1, root.GetProperty("usage").GetProperty("properties").GetInt32());
        Assert.True(root.GetProperty("canAddProperty").GetBoolean());
    }

    [Fact]
    public async Task AC8_CreateProperty_OverStarterLimit_Returns403PlanLimitReached()
    {
        var owner = NewOwner();
        // Starter limit is 3 — seed the org to exactly the limit.
        await _factory.SeedPropertyAsync(ownerId: owner);
        await _factory.SeedPropertyAsync(ownerId: owner);
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", ValidPropertyBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("plan_limit_reached", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC7_CrossOrg_GetPropertyById_Returns404_AndListExcludesIt()
    {
        var ownerA = NewOwner();
        var ownerB = NewOwner();
        var propertyA = await _factory.SeedPropertyAsync(ownerId: ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerId: ownerB); // B has its own org, no properties

        var clientB = _factory.CreateAuthenticatedClient(userId: ownerB, roles: "PropertyOwner");

        // Direct cross-org read is filtered out at the data layer → 404 (never another org's row).
        var byId = await clientB.GetAsync($"/api/properties/{propertyA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        // B's collection never contains A's property.
        var list = await clientB.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid());
        Assert.DoesNotContain(propertyA.Id, ids);
    }

    [Fact]
    public async Task AC10_Owner_StillSeesOwnRows_PostTenantScoping()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var byId = await client.GetAsync($"/api/properties/{property.Id}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);

        var list = await client.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid());
        Assert.Contains(property.Id, ids);
    }
}
