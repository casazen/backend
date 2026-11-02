using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-11a over HTTP: the alternative text of the photos of a gallery. The host saves the texts of the photos it lists and the others lose
/// theirs, the size read at the upload is never touched by a save of the texts, a text for a photo that is not in the gallery is refused and
/// saves nothing, and the public page lists each photo with its text and size next to <c>photoUrls</c>. A photo uploaded gets its size from the
/// file (the phone's EXIF orientation counted) and a deleted photo takes its text and size with it.
/// </summary>
public class PropertyPhotoAltIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";

    // A real PNG of 1 x 1 pixel.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CasazenWebApplicationFactory _factory;

    public PropertyPhotoAltIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Put_SavesTheTextsOfTheListedPhotos_ClearsTheOthers_KeepsTheSize_RefusesAPhotoOutsideTheGallery_AndThePublicPageListsThem()
    {
        var hostId = $"photoalt-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        var property = await _factory.SeedPropertyAsync(hostId);
        const string first = "https://cdn.example.test/properties/a/1.jpg";
        const string second = "https://cdn.example.test/properties/a/2.jpg";
        await SetAsync(property.Id, p =>
        {
            p.Slug = $"casa-{Guid.NewGuid():N}"[..16];
            p.CinCode = "IT058091C27G5FFZDZ";
            p.ComplianceStatus = PropertyComplianceStatus.Active;
            p.PhotoUrls = [first, second];
            p.PhotoAltJson = PropertyPhotoMeta.WithSizes(null, new Dictionary<string, (int, int)> { [first] = (1600, 1067) });
        });
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var url = $"/api/properties/{property.Id}/site-profile/photos";

        // The text of the first photo; the second is not listed, so it has none.
        var saved = await host.PutAsJsonAsync(url, new
        {
            photos = new[] { new { url = first, alt = new { it = "  Il soggiorno con il camino ", en = "The living room" } } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var photos = (await saved.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal([first, second], photos.Select(p => p.GetProperty("url").GetString()));
        Assert.Equal("Il soggiorno con il camino", photos[0].GetProperty("alt").GetProperty("it").GetString());
        Assert.Equal(1600, photos[0].GetProperty("width").GetInt32());
        Assert.Equal(1067, photos[0].GetProperty("height").GetInt32());
        Assert.Equal(JsonValueKind.Null, photos[1].GetProperty("alt").ValueKind);

        // A text for a photo that is not in the gallery, and an English text with no Italian one: refused, nothing changes.
        var stranger = await host.PutAsJsonAsync(url, new { photos = new[] { new { url = "https://cdn.example.test/other.jpg", alt = new { it = "Altro" } } } });
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal("property_photo_not_found", (await stranger.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var noItalian = await host.PutAsJsonAsync(url, new { photos = new[] { new { url = second, alt = new { en = "Only English" } } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noItalian.StatusCode);
        var problem = await noItalian.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("property_site_profile_italian_required", problem.GetProperty("code").GetString());
        Assert.Equal("photos", problem.GetProperty("field").GetString());

        // The public page: the photos with their texts and sizes, next to the same addresses in photoUrls.
        using var anonymous = _factory.CreateClient();
        var page = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties/{property.Id}");
        Assert.Equal([first, second], page.GetProperty("photoUrls").EnumerateArray().Select(u => u.GetString()));
        var listed = page.GetProperty("photos").EnumerateArray().ToList();
        Assert.Equal("The living room", listed[0].GetProperty("alt").GetProperty("en").GetString());
        Assert.Equal(1600, listed[0].GetProperty("width").GetInt32());
        Assert.Equal(JsonValueKind.Null, listed[1].GetProperty("alt").ValueKind);
        Assert.DoesNotContain("photoAltJson", page.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Sending none clears the texts and leaves the sizes.
        Assert.Equal(HttpStatusCode.OK, (await host.PutAsJsonAsync(url, new { photos = Array.Empty<object>() })).StatusCode);
        var cleared = (await host.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, cleared[0].GetProperty("alt").ValueKind);
        Assert.Equal(1600, cleared[0].GetProperty("width").GetInt32());
    }

    [PostgresFact]
    public async Task Upload_ReadsTheSizeFromTheFile_AndDeletingThePhotoTakesItsTextAndSizeWithIt()
    {
        var hostId = $"photoalt-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var site = $"/api/properties/{property.Id}/site-profile/photos";

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "images", "a.png");
        var uploaded = await host.PostAsync($"/api/properties/{property.Id}/images", form);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var gallery = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photoUrls").EnumerateArray().Select(u => u.GetString()!).ToList();
        var photo = Assert.Single(gallery);

        // The size is read from the PNG header; a text is added to the same photo.
        var withSize = (await host.GetFromJsonAsync<JsonElement>(site)).EnumerateArray().Single();
        Assert.Equal((1, 1), (withSize.GetProperty("width").GetInt32(), withSize.GetProperty("height").GetInt32()));
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.PutAsJsonAsync(site, new { photos = new[] { new { url = photo, alt = new { it = "La facciata" } } } })).StatusCode);
        Assert.NotNull((await LoadAsync(property.Id)).PhotoAltJson);

        Assert.Equal(HttpStatusCode.OK, (await host.DeleteAsync($"/api/properties/{property.Id}/images?url={Uri.EscapeDataString(photo)}")).StatusCode);

        var after = await LoadAsync(property.Id);
        Assert.Empty(after.PhotoUrls);
        Assert.Null(after.PhotoAltJson);
    }

    private async Task<Property> LoadAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private async Task SetAsync(Guid propertyId, Action<Property> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
    }
}
