using System.Net;
using System.Net.Http.Headers;
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
/// DB-11a over HTTP: the points of interest and the floor plan of a property. A place is never told more exactly than the privacy rule allows
/// (the side of the compass is one of eight points, the distance is rounded and not closer than 200 m, and nothing here is a coordinate), the list
/// the host sends replaces the stored one and a mistake saves nothing, and the public page shows both. A floor plan level is a PNG or JPEG checked on
/// its content, its size is read from the file, and deleting it removes the row.
/// </summary>
public class PropertySiteMediaIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";

    // A real PNG of 1 x 1 pixel.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CasazenWebApplicationFactory _factory;

    public PropertySiteMediaIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Pois_AreStoredWithTheDistanceRoundedAndNotCloserThan200m_TheListReplacesTheStoredOne_AMistakeSavesNothing_AndThePublicPageShowsThem()
    {
        var (hostId, org, property) = await SeedPublishedPropertyAsync();
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var url = $"/api/properties/{property.Id}/site-profile/pois";

        var saved = await host.PutAsJsonAsync(url, new
        {
            pois = new object[]
            {
                new { kind = "Beach", name = new { it = "Spiaggia di Costa Merlata", en = "Costa Merlata beach" }, mode = "Car", minutes = 20, direction = "NE", distanceMeters = 14_230 },
                new { kind = "Food", name = new { it = "Trattoria" }, mode = "Walk", minutes = 2, direction = "S", distanceMeters = 30 },
                new { kind = "Sight", name = new { it = "Il borgo" }, mode = "Walk", minutes = 12, direction = "N", distanceMeters = 1_130 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var raw = await saved.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();

        // 14,230 m is told to the half kilometre, 30 m is not told closer than 200 m, 1,130 m to the quarter kilometre; the side is one of eight points.
        Assert.Equal([14_000, 200, 1_250], list.Select(p => p.GetProperty("distanceMeters").GetInt32()));
        Assert.Equal(["NE", "S", "N"], list.Select(p => p.GetProperty("direction").GetString()));
        Assert.DoesNotContain("latitude", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", raw, StringComparison.OrdinalIgnoreCase);

        // A name with no Italian part, and more than twelve: refused, the stored list stays as it was.
        var noItalian = await host.PutAsJsonAsync(url, new { pois = new[] { new { kind = "Shop", name = new { en = "Shop" }, mode = "Car", minutes = 5, direction = "E", distanceMeters = 3_000 } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noItalian.StatusCode);
        Assert.Equal("property_site_profile_italian_required", (await noItalian.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var tooMany = await host.PutAsJsonAsync(
            url,
            new { pois = Enumerable.Range(0, 13).Select(i => new { kind = "Shop", name = new { it = $"Negozio {i}" }, mode = "Car", minutes = 5, direction = "E", distanceMeters = 3_000 }).ToArray() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal("property_poi_limit_reached", (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(3, (await host.GetFromJsonAsync<JsonElement>(url)).GetArrayLength());

        // The public page: the same points, with no position of any kind.
        using var anonymous = _factory.CreateClient();
        var page = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties/{property.Id}");
        var pois = page.GetProperty("profile").GetProperty("pois").EnumerateArray().ToList();
        Assert.Equal("Costa Merlata beach", pois[0].GetProperty("name").GetProperty("en").GetString());
        Assert.Equal(14_000, pois[0].GetProperty("distanceMeters").GetInt32());
        Assert.Equal(["kind", "name", "mode", "minutes", "direction", "distanceMeters"], pois[0].EnumerateObject().Select(f => f.Name));

        // The list replaces the stored one; an empty list removes every point.
        Assert.Equal(HttpStatusCode.OK, (await host.PutAsJsonAsync(url, new { pois = Array.Empty<object>() })).StatusCode);
        Assert.Equal(0, (await host.GetFromJsonAsync<JsonElement>(url)).GetArrayLength());
    }

    [Fact]
    public async Task FloorPlan_AnImageIsCheckedOnItsContent_ItsSizeIsRead_ThePublicPageShowsTheLevelsLowestFirst_AndDeletingRemovesTheLevel()
    {
        var (hostId, org, property) = await SeedPublishedPropertyAsync();
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var url = $"/api/properties/{property.Id}/site-profile/floor-plans";

        // A text file named like an image is refused on its content; nothing is stored.
        using (var fake = Form(System.Text.Encoding.UTF8.GetBytes("not an image"), "image/png", "piano.png", "Piano terra"))
        {
            var refused = await host.PostAsync(url, fake);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("property_floor_plan_invalid_type", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // No Italian name: refused too.
        using (var unnamed = Form(PngBytes, "image/png", "piano.png", nameIt: null))
        {
            var refused = await host.PostAsync(url, unnamed);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("property_site_profile_italian_required", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        Assert.Equal(0, (await host.GetFromJsonAsync<JsonElement>(url)).GetArrayLength());

        // Two real levels, the upper one first: the page lists the lowest floor first and each with the size read from its file.
        using (var upper = Form(PngBytes, "image/png", "primo.png", "Primo piano", level: 1, areaSqm: 45))
            Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(url, upper)).StatusCode);
        using (var ground = Form(PngBytes, "image/png", "terra.png", "Piano terra", level: 0))
        {
            var added = await host.PostAsync(url, ground);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        using var anonymous = _factory.CreateClient();
        var page = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties/{property.Id}");
        var plans = page.GetProperty("profile").GetProperty("floorPlans").EnumerateArray().ToList();
        Assert.Equal(["Piano terra", "Primo piano"], plans.Select(p => p.GetProperty("name").GetProperty("it").GetString()));
        Assert.Equal((1, 1), (plans[0].GetProperty("width").GetInt32(), plans[0].GetProperty("height").GetInt32()));
        Assert.Contains("/floorplans/", plans[0].GetProperty("imageUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(45, plans[1].GetProperty("areaSqm").GetInt32());

        // Deleting a level removes it.
        var id = plans[1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"{url}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.DeleteAsync($"{url}/{id}")).StatusCode);
        Assert.Equal(1, (await host.GetFromJsonAsync<JsonElement>(url)).GetArrayLength());
    }

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string fileName, string? nameIt, int? level = null, int? areaSqm = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", fileName);
        if (nameIt is not null)
            form.Add(new StringContent(nameIt), "nameIt");
        if (level is { } floor)
            form.Add(new StringContent(floor.ToString(System.Globalization.CultureInfo.InvariantCulture)), "level");
        if (areaSqm is { } area)
            form.Add(new StringContent(area.ToString(System.Globalization.CultureInfo.InvariantCulture)), "areaSqm");

        return form;
    }

    private async Task<(string HostId, OrgEntity Org, Property Property)> SeedPublishedPropertyAsync()
    {
        var hostId = $"sitemedia-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(hostId);
        var seeded = await _factory.SeedPropertyAsync(hostId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.Id);
        property.Slug = $"casa-{Guid.NewGuid():N}"[..16];
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        var org = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == property.OrgId);
        return (hostId, org, property);
    }
}
