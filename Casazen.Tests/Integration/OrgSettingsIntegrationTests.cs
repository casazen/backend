using System.Net;
using System.Net.Http.Json;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        var otherSlug = await SetSlugAsync(otherOrg.Id, $"villa-presa-{Guid.NewGuid():N}"[..30]);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Mine",
            slug = otherSlug,
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

    [Fact]
    public async Task UpdateMySettings_SlugChanged_OldPublicLinkResolvesToTheOrgWithItsNewSlug()
    {
        var ownerId = $"auth0|settings-alias-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var oldSlug = await SetSlugAsync(org.Id, $"villa-vecchia-{Guid.NewGuid():N}"[..30]);
        var newSlug = $"villa-nuova-{Guid.NewGuid():N}"[..30];

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Villa Nuova",
            slug = newSlug,
            contactEmail = "host@villanuova.it",
            contactEmailPublic = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var anonymousClient = _factory.CreateClient();
        var byOld = await anonymousClient.GetAsync($"/api/public/orgs/{oldSlug}");
        Assert.Equal(HttpStatusCode.OK, byOld.StatusCode);
        var json = await byOld.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(newSlug, json.GetProperty("slug").GetString());
        Assert.Equal("Villa Nuova", json.GetProperty("displayName").GetString());

        // The old slug stays reserved to this org.
        var otherId = $"auth0|settings-alias-other-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherId);
        using var otherClient = _factory.CreateAuthenticatedClient(otherId, "PropertyOwner");
        var takeOver = await otherClient.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Other",
            slug = oldSlug,
            contactEmail = "other@example.com",
            contactEmailPublic = false,
        });
        Assert.Equal(HttpStatusCode.Conflict, takeOver.StatusCode);
    }

    [Fact]
    public async Task GetMe_AfterSlugChange_PublicSiteUrlUsesTheNewSlugOnTheConfiguredBaseUrl()
    {
        var ownerId = $"auth0|settings-share-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetSlugAsync(org.Id, $"villa-prima-{Guid.NewGuid():N}"[..30]);
        var newSlug = $"villa-dopo-{Guid.NewGuid():N}"[..30];

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var update = await client.PutAsJsonAsync("/api/orgs/me/settings", new
        {
            name = "Villa Dopo",
            slug = newSlug,
            contactEmail = "host@villadopo.it",
            contactEmailPublic = false,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var me = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/users/me");

        // MO-11 (A6-09, A3-22): the app shares {publicSiteUrl}/property/{slug}; the base is App:PublicSiteBaseUrl (D3).
        var orgJson = me.GetProperty("org");
        Assert.Equal(newSlug, orgJson.GetProperty("slug").GetString());
        Assert.Equal($"https://casazen-app.vercel.app/book/{newSlug}", orgJson.GetProperty("publicSiteUrl").GetString());
    }

    [Fact]
    public async Task GetMe_PublicSiteBaseUrlMissing_PublicSiteUrlIsNull()
    {
        await using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicSiteBaseUrl"] = "" })));
        var ownerId = $"auth0|settings-share-unset-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", ownerId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "PropertyOwner");

        var me = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/users/me");

        // No fallback domain (D3): without the public URL the app gets no link to share instead of a broken one.
        var orgJson = me.GetProperty("org");
        Assert.Equal(org.Slug, orgJson.GetProperty("slug").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, orgJson.GetProperty("publicSiteUrl").ValueKind);
    }

    [Fact]
    public async Task GetSlugAvailability_SlugOfAnotherOrg_ReturnsUnavailableWithReason()
    {
        var ownerId = $"auth0|settings-availability-{Guid.NewGuid():N}";
        var otherId = $"auth0|settings-availability-other-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);
        var otherOrg = await _factory.SeedOrgForOwnerAsync(otherId);
        var otherSlug = await SetSlugAsync(otherOrg.Id, $"villa-presa-{Guid.NewGuid():N}"[..30]);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var taken = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/orgs/me/settings/slug-availability?slug={Uri.EscapeDataString(otherSlug)}");
        var free = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/orgs/me/settings/slug-availability?slug={Uri.EscapeDataString("Villa Libera " + Guid.NewGuid().ToString("N")[..8])}");

        Assert.False(taken.GetProperty("available").GetBoolean());
        Assert.Equal("org_slug_taken", taken.GetProperty("code").GetString());
        Assert.True(free.GetProperty("available").GetBoolean());
        Assert.StartsWith("villa-libera-", free.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task GetSlugAvailability_AsStaffRole_Returns403()
    {
        var userId = $"auth0|settings-availability-staff-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        var response = await client.GetAsync("/api/orgs/me/settings/slug-availability?slug=villa-mare");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [PostgresFact]
    public async Task UpdateMySettings_TwoOrgsRaceForTheSameSlug_OneGetsItTheOther409()
    {
        var firstId = $"auth0|settings-race-a-{Guid.NewGuid():N}";
        var secondId = $"auth0|settings-race-b-{Guid.NewGuid():N}";
        var firstOrg = await _factory.SeedOrgForOwnerAsync(firstId);
        var secondOrg = await _factory.SeedOrgForOwnerAsync(secondId);
        var slug = $"villa-contesa-{Guid.NewGuid():N}"[..30];

        using var firstClient = _factory.CreateAuthenticatedClient(firstId, "PropertyOwner");
        using var secondClient = _factory.CreateAuthenticatedClient(secondId, "PropertyOwner");
        var body = new { name = "Contesa", slug, contactEmail = "host@example.com", contactEmailPublic = false };

        var responses = await Task.WhenAll(
            firstClient.PutAsJsonAsync("/api/orgs/me/settings", body),
            secondClient.PutAsJsonAsync("/api/orgs/me/settings", body));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
            responses.Select(r => r.StatusCode).Order().ToArray());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owners = await db.Orgs.AsNoTracking()
            .Where(o => (o.Id == firstOrg.Id || o.Id == secondOrg.Id) && o.Slug == slug)
            .CountAsync();
        Assert.Equal(1, owners);
    }

    /// <summary>The seeded slug (<c>test-org-{sub}</c>) is a legacy one; the conflict tests need one a host could choose.</summary>
    private async Task<string> SetSlugAsync(Guid orgId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.Slug = slug;
        await db.SaveChangesAsync();
        return slug;
    }
}
