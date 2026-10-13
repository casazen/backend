using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// MO-12 (A6-20) on real PostgreSQL through the whole pipeline: <c>GET /api/properties</c> and
/// <c>GET /api/properties/cin-compliance</c> follow the caller's TN-3 scope. A PropertyManager of the org (who gets the
/// org's booking pushes) sees every property of the org and its CIN summary; an owner only the properties they own;
/// nobody another org's. The list rows are the property record, never the entity.
/// </summary>
public class PropertyRoleVisibilityPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ValidCin = "IT058091C27G5FFZDZ";
    private readonly CasazenWebApplicationFactory _factory;

    public PropertyRoleVisibilityPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task PropertiesAndCinCompliance_ManagerOwnerAndOtherOrg_SeeTheirScopeOnly()
    {
        var ownerId = $"auth0|mo12-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var colleagueId = $"auth0|mo12-colleague-{Guid.NewGuid():N}";
        var managerId = $"auth0|mo12-manager-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(colleagueId, org.Id);
        await SeedUserInOrgAsync(managerId, org.Id);
        var ownerProperty = await SeedPropertyAsync(org.Id, ownerId, "A owner", ValidCin);
        var colleagueProperty = await SeedPropertyAsync(org.Id, colleagueId, "B colleague", cinCode: null);

        var otherHostId = $"auth0|mo12-other-{Guid.NewGuid():N}";
        var otherOrg = await _factory.SeedOrgForOwnerAsync(otherHostId);
        var otherProperty = await SeedPropertyAsync(otherOrg.Id, otherHostId, "C other org", cinCode: null);

        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        using var manager = _factory.CreateAuthenticatedClient(managerId, "PropertyOwner,PropertyManager");
        using var other = _factory.CreateAuthenticatedClient(otherHostId, "PropertyOwner");

        // The manager owns nothing, yet sees the whole org (it receives the org's pushes): never "no property".
        Assert.Equal([ownerProperty.Id, colleagueProperty.Id], await ListIdsAsync(manager, "/api/properties"));
        Assert.Equal([ownerProperty.Id], await ListIdsAsync(owner, "/api/properties"));
        Assert.Equal([otherProperty.Id], await ListIdsAsync(other, "/api/properties"));

        // The CIN summary has the same reach as the list.
        var managerCin = await GetJsonAsync(manager, "/api/properties/cin-compliance");
        Assert.Equal(2, managerCin.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, managerCin.GetProperty("summary").GetProperty("valid").GetInt32());
        Assert.Equal(1, managerCin.GetProperty("summary").GetProperty("missing").GetInt32());
        Assert.Equal(
            [ownerProperty.Id, colleagueProperty.Id],
            managerCin.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("propertyId").GetGuid()));

        var ownerCin = await GetJsonAsync(owner, "/api/properties/cin-compliance");
        Assert.Equal(1, ownerCin.GetProperty("totalCount").GetInt32());
        Assert.Equal(ownerProperty.Id, ownerCin.GetProperty("items")[0].GetProperty("propertyId").GetGuid());
        Assert.False(ownerCin.GetProperty("summary").GetProperty("hasNonCompliant").GetBoolean());

        var otherCin = await GetJsonAsync(other, "/api/properties/cin-compliance");
        Assert.Equal(otherProperty.Id, Assert.Single(otherCin.GetProperty("items").EnumerateArray()).GetProperty("propertyId").GetGuid());
    }

    [PostgresFact]
    public async Task GetAll_Row_IsThePropertyRecordWithoutNavigations()
    {
        var ownerId = $"auth0|mo12-dto-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SeedPropertyAsync(org.Id, ownerId, "Casa DTO", ValidCin);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");

        var list = await GetJsonAsync(owner, "/api/properties");

        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal("Casa DTO", row.GetProperty("name").GetString());
        Assert.Equal(ValidCin, row.GetProperty("cinCode").GetString());
        Assert.False(row.GetProperty("isPaused").GetBoolean());
        // The entity's navigations (bookings, OTA integrations, documents, org) are not part of the list contract.
        Assert.False(row.TryGetProperty("bookings", out _));
        Assert.False(row.TryGetProperty("otaIntegrations", out _));
        Assert.False(row.TryGetProperty("propertyDocuments", out _));
        Assert.False(row.TryGetProperty("org", out _));
    }

    private static async Task<IReadOnlyList<Guid>> ListIdsAsync(HttpClient client, string url) =>
        (await GetJsonAsync(client, url)).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Property> SeedPropertyAsync(Guid orgId, string ownerId, string name, string? cinCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = orgId,
            Name = name,
            Address = $"Via MO12 {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = cinCode,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task SeedUserInOrgAsync(string userId, Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Host",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }
}
