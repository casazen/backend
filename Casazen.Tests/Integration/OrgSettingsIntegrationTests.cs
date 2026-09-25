using System.Net;
using System.Net.Http.Json;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// GET/PUT /api/orgs/me/settings (A1-22, A1-23): editable org identity, its public slug, and the GDPR opt-in
/// that gates the contact email on the anonymous public booking site.
/// </summary>
public class OrgSettingsIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public OrgSettingsIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetMySettings_AsOwner_Returns200WithSettings()
    {
        var ownerId = $"auth0|settings-get-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync("/api/orgs/me/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(org.Id, json.GetProperty("id").GetGuid());
        Assert.Equal(org.Slug, json.GetProperty("slug").GetString());
        // Off by default (GDPR opt-in): a freshly provisioned org never publishes its contact email.
        Assert.False(json.GetProperty("contactEmailPublic").GetBoolean());
    }

    [Fact]
    public async Task GetMySettings_AsStaffRole_Returns403()
    {
        var userId = $"auth0|settings-staff-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        var response = await client.GetAsync("/api/orgs/me/settings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateMySettings_ValidInput_UpdatesNameSlugAndContactEmail()
    {
        var ownerId = $"auth0|settings-update-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);
        var newSlug = $"villa-parco-{Guid.NewGuid():N}"[..30];

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Villa Parco Rentals",
            slug = newSlug,
            contactEmail = "host@villaparco.it",
            contactEmailPublic = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Villa Parco Rentals", json.GetProperty("name").GetString());
        Assert.Equal(newSlug, json.GetProperty("slug").GetString());
        Assert.Equal("host@villaparco.it", json.GetProperty("contactEmail").GetString());
        Assert.True(json.GetProperty("contactEmailPublic").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(json.GetProperty("id").GetGuid());
        Assert.Equal("Villa Parco Rentals", persisted!.DisplayName);
    }

    [Fact]
    public async Task UpdateMySettings_SlugAlreadyUsedByAnotherOrg_Returns409AndDoesNotChangeSlug()
    {
        var ownerId = $"auth0|settings-conflict-owner-{Guid.NewGuid():N}";
        var otherId = $"auth0|settings-conflict-other-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var otherOrg = await _factory.SeedOrgForOwnerAsync(otherId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Mine",
            slug = otherOrg.Slug,
            contactEmail = "mine@example.com",
            contactEmailPublic = false,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("org_slug_taken", json.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(org.Id);
        Assert.Equal(org.Slug, persisted!.Slug);
    }

    [Fact]
    public async Task UpdateMySettings_ReservedSlug_Returns422()
    {
        var ownerId = $"auth0|settings-reserved-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Mine",
            slug = "admin",
            contactEmail = "mine@example.com",
            contactEmailPublic = false,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("org_slug_reserved", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateMySettings_AsStaffRole_Returns403_AndDoesNotMutateSettings()
    {
        var userId = $"auth0|settings-staff-write-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Hijacked",
            slug = "staff-hijack",
            contactEmail = "hijack@example.com",
            contactEmailPublic = true,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(org.Id);
        Assert.Equal(org.Slug, persisted!.Slug);
        Assert.False(persisted.ContactEmailPublic);
    }

    [Fact]
    public async Task UpdateMySettings_MissingContactEmail_Returns400ValidationError()
    {
        var ownerId = $"auth0|settings-missing-email-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Mine",
            slug = "villa-mine",
            contactEmail = "",
            contactEmailPublic = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ContactEmailPublicOptIn_ControlsVisibilityOnThePublicEndpoint()
    {
        var ownerId = $"auth0|settings-public-optin-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = $"opt-in-org-{Guid.NewGuid():N}"[..30];

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");

        // Off by default: even after setting a contact email, the public page must not show it.
        var offResponse = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Opt-in Org",
            slug,
            contactEmail = "private@example.com",
            contactEmailPublic = false,
        });
        Assert.Equal(HttpStatusCode.OK, offResponse.StatusCode);

        using var anonymousClient = _factory.CreateClient();
        var publicResponseOff = await anonymousClient.GetAsync($"/api/public/orgs/{slug}");
        Assert.Equal(HttpStatusCode.OK, publicResponseOff.StatusCode);
        var publicJsonOff = await publicResponseOff.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(
            publicJsonOff.TryGetProperty("contactEmail", out var offValue) && offValue.ValueKind == System.Text.Json.JsonValueKind.Null,
            "contactEmail must be null when the org has not opted in.");

        // Explicit opt-in: now the public page shows it.
        var onResponse = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Opt-in Org",
            slug,
            contactEmail = "private@example.com",
            contactEmailPublic = true,
        });
        Assert.Equal(HttpStatusCode.OK, onResponse.StatusCode);

        var publicResponseOn = await anonymousClient.GetAsync($"/api/public/orgs/{slug}");
        Assert.Equal(HttpStatusCode.OK, publicResponseOn.StatusCode);
        var publicJsonOn = await publicResponseOn.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("private@example.com", publicJsonOn.GetProperty("contactEmail").GetString());
    }
}
