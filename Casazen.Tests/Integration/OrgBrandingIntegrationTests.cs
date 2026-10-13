using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Branding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// <c>/api/orgs/me/branding</c> (BK-12, A3-17): the org's billing/settings administrator edits logo, hero, primary
/// color, tagline and theme, and the anonymous public endpoints (<c>/api/public/orgs/{slug}</c>,
/// <c>/api/public/resolve-host</c>) serve them.
/// </summary>
public class OrgBrandingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public OrgBrandingIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetBranding_NewOrg_ReturnsDefaultThemeAndNoImages()
    {
        var ownerId = $"auth0|branding-get-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.GetAsync("/api/orgs/me/branding");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mare", json.GetProperty("publicThemeId").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("logoUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("primaryColor").ValueKind);
        Assert.Equal(org.Slug, json.GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/orgs/me/branding")]
    [InlineData("PUT", "/api/orgs/me/branding")]
    [InlineData("DELETE", "/api/orgs/me/branding/logo")]
    public async Task BrandingEndpoints_AsStaffRole_Return403(string method, string path)
    {
        var userId = $"auth0|branding-staff-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { primaryColor = "#123456" }) : null,
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBranding_ValidValues_ArePublishedOnPublicOrgEndpoint()
    {
        var ownerId = $"auth0|branding-put-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = await SetSlugAsync(org.Id, $"villa-brand-{Guid.NewGuid():N}"[..28]);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new
        {
            primaryColor = "#1A6B8F",
            publicThemeId = "montagna",
            tagline = "Baite  in quota",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("#1a6b8f", saved.GetProperty("primaryColor").GetString());

        using var anonymous = _factory.CreateClient();
        var publicOrg = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}");
        Assert.Equal("#1a6b8f", publicOrg.GetProperty("primaryColor").GetString());
        Assert.Equal("#1a6b8f", publicOrg.GetProperty("themeColor").GetString());
        Assert.Equal("montagna", publicOrg.GetProperty("publicThemeId").GetString());
        Assert.Equal("Baite in quota", publicOrg.GetProperty("tagline").GetString());
    }

    [Fact]
    public async Task UpdateBranding_InvalidColor_Returns422AndKeepsStoredValues()
    {
        var ownerId = $"auth0|branding-bad-color-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new
        {
            primaryColor = "red;}body{x",
            publicThemeId = "urban",
            tagline = "x",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_branding_color_invalid", json.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Null(stored.ThemeColor);
        Assert.Null(stored.PublicThemeId);
    }

    [Fact]
    public async Task UpdateBranding_UnsupportedTheme_Returns422()
    {
        var ownerId = $"auth0|branding-bad-theme-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new { publicThemeId = "collina" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_branding_theme_invalid", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UploadLogo_ValidPng_StoresPublicObjectAndPublishesAbsoluteUrl()
    {
        var ownerId = $"auth0|branding-logo-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = await SetSlugAsync(org.Id, $"villa-logo-{Guid.NewGuid():N}"[..27]);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsync("/api/orgs/me/branding/logo", ImageForm(TestImageBytes.Png(400, 120), "logo.png", "image/png"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var logoUrl = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").GetString()!;
        Assert.True(Uri.TryCreate(logoUrl, UriKind.Absolute, out _));
        Assert.Contains($"/orgs/{org.Id}/branding/logo/", logoUrl);

        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var key = storage.TryGetPublicKey(logoUrl);
        Assert.NotNull(key);
        Assert.True(await storage.ExistsAsync(StorageBucket.Public, key!));

        using var anonymous = _factory.CreateClient();
        var publicOrg = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}");
        Assert.Equal(logoUrl, publicOrg.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task UploadLogo_SvgDeclaredAsPng_Returns422TypeInvalid()
    {
        var ownerId = $"auth0|branding-svg-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsync("/api/orgs/me/branding/logo", ImageForm(TestImageBytes.Svg(), "logo.png", "image/png"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_branding_image_type_invalid", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UploadHero_TooSmall_Returns422DimensionsInvalid()
    {
        var ownerId = $"auth0|branding-hero-small-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsync("/api/orgs/me/branding/hero", ImageForm(TestImageBytes.Jpeg(800, 300), "hero.jpg", "image/jpeg"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_branding_image_dimensions_invalid", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UploadLogo_WithoutFile_Returns422ImageEmpty()
    {
        var ownerId = $"auth0|branding-no-file-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        using var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        var response = await client.PutAsync("/api/orgs/me/branding/logo", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_branding_image_empty", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoveHero_AfterUpload_ClearsUrlAndDeletesObject()
    {
        var ownerId = $"auth0|branding-hero-remove-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var upload = await client.PutAsync("/api/orgs/me/branding/hero", ImageForm(TestImageBytes.WebPExtended(2400, 900), "hero.webp", "image/webp"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var heroUrl = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heroImageUrl").GetString()!;

        var response = await client.DeleteAsync("/api/orgs/me/branding/hero");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, json.GetProperty("heroImageUrl").ValueKind);
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        Assert.False(await storage.ExistsAsync(StorageBucket.Public, storage.TryGetPublicKey(heroUrl)!));
    }

    [Fact]
    public async Task UpdateBranding_AfterHostWasResolved_ResolveHostServesNewBranding()
    {
        var ownerId = $"auth0|branding-host-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = await SetSlugAsync(org.Id, $"villa-host-{Guid.NewGuid():N}"[..27]);
        using var anonymous = _factory.CreateClient();
        var host = $"/api/public/resolve-host?host={slug}.casazen.it";
        var before = await anonymous.GetFromJsonAsync<JsonElement>(host); // cached from here on
        Assert.Equal(JsonValueKind.Null, before.GetProperty("branding").GetProperty("tagline").ValueKind);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new { publicThemeId = "urban", tagline = "Nel cuore della città" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await anonymous.GetFromJsonAsync<JsonElement>(host);
        Assert.Equal("Nel cuore della città", after.GetProperty("branding").GetProperty("tagline").GetString());
        Assert.Equal("urban", after.GetProperty("branding").GetProperty("publicThemeId").GetString());
    }

    // ─── Public profile of the booking site (DB-03) ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateBranding_PublicProfile_IsPublishedOnThePublicOrgWithTheNormalizedPhone()
    {
        var ownerId = $"auth0|branding-profile-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = await SetSlugAsync(org.Id, $"villa-profile-{Guid.NewGuid():N}"[..29]);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new
        {
            tagline = "Case con l'anima",
            subtitle = "  Trulli, case sul mare\n e dimore barocche. ",
            hostName = " Giulia   Rinaldi ",
            publicPhone = "+39 333 123 4567",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Trulli, case sul mare e dimore barocche.", saved.GetProperty("subtitle").GetString());
        Assert.Equal("Giulia Rinaldi", saved.GetProperty("hostName").GetString());
        Assert.Equal("+393331234567", saved.GetProperty("publicPhone").GetString());

        using var anonymous = _factory.CreateClient();
        var publicOrg = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}");
        Assert.Equal("Trulli, case sul mare e dimore barocche.", publicOrg.GetProperty("subtitle").GetString());
        Assert.Equal("Giulia Rinaldi", publicOrg.GetProperty("hostName").GetString());
        Assert.Equal("+393331234567", publicOrg.GetProperty("publicPhone").GetString());
        // The branding that was already there is untouched.
        Assert.Equal("Case con l'anima", publicOrg.GetProperty("tagline").GetString());
    }

    [Fact]
    public async Task UpdateBranding_BodyWithoutTheProfile_KeepsTheProfile()
    {
        // The appearance form of today sends color, theme and tagline only; it must never erase what the profile holds.
        var ownerId = $"auth0|branding-keep-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);
        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync("/api/orgs/me/branding", new { subtitle = "Sottotitolo", hostName = "Giulia", publicPhone = "0832123456" })).StatusCode);

        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new { primaryColor = "#1A6B8F", publicThemeId = "urban", tagline = "Nuovo slogan" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nuovo slogan", json.GetProperty("tagline").GetString());
        Assert.Equal("Sottotitolo", json.GetProperty("subtitle").GetString());
        Assert.Equal("Giulia", json.GetProperty("hostName").GetString());
        Assert.Equal("0832123456", json.GetProperty("publicPhone").GetString());
        var get = await client.GetFromJsonAsync<JsonElement>("/api/orgs/me/branding");
        Assert.Equal("0832123456", get.GetProperty("publicPhone").GetString());
    }

    [Fact]
    public async Task UpdateBranding_ProfileSentAsNullOrBlank_UnpublishesIt()
    {
        var ownerId = $"auth0|branding-clear-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = await SetSlugAsync(org.Id, $"villa-clear-{Guid.NewGuid():N}"[..27]);
        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        await client.PutAsJsonAsync("/api/orgs/me/branding", new { subtitle = "Sottotitolo", hostName = "Giulia", publicPhone = "0832123456" });

        var response = await client.PutAsync(
            "/api/orgs/me/branding",
            new StringContent("""{ "subtitle": null, "publicPhone": "  " }""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var anonymous = _factory.CreateClient();
        var publicOrg = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}");
        Assert.Equal(JsonValueKind.Null, publicOrg.GetProperty("subtitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, publicOrg.GetProperty("publicPhone").ValueKind);
        Assert.Equal("Giulia", publicOrg.GetProperty("hostName").GetString());
    }

    [Theory]
    [InlineData("publicPhone", "call me maybe", "org_branding_phone_invalid")]
    [InlineData("publicPhone", "12345", "org_branding_phone_invalid")]
    [InlineData("hostName", "h", "org_branding_host_name_too_long")]
    [InlineData("subtitle", "s", "org_branding_subtitle_too_long")]
    public async Task UpdateBranding_InvalidProfileValue_Returns422WithItsCodeAndKeepsTheStoredProfile(string field, string value, string expectedCode)
    {
        var ownerId = $"auth0|branding-bad-profile-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);
        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        await client.PutAsJsonAsync("/api/orgs/me/branding", new { hostName = "Giulia", publicPhone = "0832123456" });
        // One character stands for "too long" in the rows above: the real overflow is built here.
        var sent = field switch
        {
            "hostName" => new string('h', 101),
            "subtitle" => new string('s', 301),
            _ => value,
        };

        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new Dictionary<string, string> { [field] = sent });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString()));
        var stored = await client.GetFromJsonAsync<JsonElement>("/api/orgs/me/branding");
        Assert.Equal("Giulia", stored.GetProperty("hostName").GetString());
        Assert.Equal("0832123456", stored.GetProperty("publicPhone").GetString());
    }

    [Theory]
    [InlineData("subtitle", 501)]
    [InlineData("hostName", 201)]
    [InlineData("publicPhone", 41)]
    public async Task UpdateBranding_InputFarBeyondTheLimits_Returns400ValidationError(string field, int length)
    {
        var ownerId = $"auth0|branding-huge-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);
        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/branding", new Dictionary<string, string> { [field] = new string('1', length) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetBranding_NewOrg_HasNoPublicProfile()
    {
        var ownerId = $"auth0|branding-empty-profile-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var json = await client.GetFromJsonAsync<JsonElement>("/api/orgs/me/branding");

        Assert.Equal(JsonValueKind.Null, json.GetProperty("subtitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("hostName").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("publicPhone").ValueKind);
    }

    private static MultipartFormDataContent ImageForm(byte[] bytes, string fileName, string contentType)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private async Task<string> SetSlugAsync(Guid orgId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.Slug = slug;
        // The slug is also the label of the org's subdomain: only an org that chose the subdomain mode is served on it (BK-16).
        org.PublicHostMode = PublicHostMode.CasazenSubdomain;
        org.Subdomain = slug;
        await db.SaveChangesAsync();
        return slug;
    }
}
