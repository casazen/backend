using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-04 (A2-26) over HTTP on PostgreSQL: the host gallery endpoints (upload, order, cover, deletion) store the photos in
/// the public bucket, keep the order that the public pages read (<c>photoUrls[0]</c> is the cover), remove the object on
/// deletion, refuse another org's property and a colleague without ownership, and never lose a photo when changes of the
/// same property run in parallel (advisory lock). The Testing host uses the filesystem storage.
/// </summary>
public class PropertyPhotosPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    private readonly CasazenWebApplicationFactory _factory;

    public PropertyPhotosPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewUser() => $"auth0|photos-{Guid.NewGuid():N}";

    [PostgresFact]
    public async Task Gallery_UploadOrderCoverAndDelete_KeepsThePublicPageInGalleryOrderAndRemovesTheObject()
    {
        var ownerId = NewUser();
        var property = await SeedPublishedPropertyAsync(ownerId);
        using var owner = Host(ownerId);

        var first = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes), ("b.jpg", "image/jpeg", JpegBytes));
        Assert.Equal(2, first.Count);
        Assert.EndsWith(".png", first[0]);
        Assert.EndsWith(".jpg", first[1]);

        // Public readers (search card, property page, org site) show photoUrls[0] first: the cover.
        Assert.Equal(first, await PublicPhotosAsync(property.Id));

        var reordered = await ReadPhotosAsync(await owner.PutAsJsonAsync($"/api/properties/{property.Id}/images/order", new[] { first[1], first[0] }));
        Assert.Equal([first[1], first[0]], reordered);
        Assert.Equal([first[1], first[0]], await PublicPhotosAsync(property.Id));

        var third = (await UploadAsync(owner, property.Id, ("c.png", "image/png", PngBytes)))[^1];
        var cover = await ReadPhotosAsync(await owner.PutAsJsonAsync($"/api/properties/{property.Id}/images/cover", new { url = third }));
        Assert.Equal(third, cover[0]);
        Assert.Equal(cover, await PublicPhotosAsync(property.Id));

        var coverKey = KeyOf(third);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(new Uri(third).PathAndQuery)).StatusCode);

        var afterDelete = await ReadPhotosAsync(await owner.DeleteAsync($"/api/properties/{property.Id}/images?url={Uri.EscapeDataString(third)}"));

        Assert.Equal([first[1], first[0]], afterDelete);
        Assert.Equal(afterDelete, await PublicPhotosAsync(property.Id));
        Assert.False(File.Exists(StoragePath(coverKey)));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri(third).PathAndQuery)).StatusCode);
    }

    [PostgresFact]
    public async Task GetImages_ReturnsTheGalleryAndTheRulesTheUploadEnforces()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var uploaded = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes));

        var response = await owner.GetAsync($"/api/properties/{property.Id}/images");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(uploaded, body.RootElement.GetProperty("photoUrls").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(PropertyPhotoLimits.MaxPhotos, body.RootElement.GetProperty("maxPhotos").GetInt32());
        Assert.Equal(PropertyPhotoLimits.MaxFileSizeBytes, body.RootElement.GetProperty("maxFileSizeBytes").GetInt64());
        Assert.Contains("image/webp", body.RootElement.GetProperty("allowedContentTypes").EnumerateArray().Select(e => e.GetString()));
    }

    [PostgresFact]
    public async Task Upload_TextRenamedToJpg_Returns422WithStableCodeAndStoresNothing()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);

        var response = await owner.PostAsync(
            $"/api/properties/{property.Id}/images", Form(("trucco.jpg", "image/jpeg", "<script>alert(1)</script>"u8.ToArray())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("property_photo_invalid_type", await CodeOfAsync(response));
        Assert.Empty(await StoredPhotosOfAsync(property.Id));
        Assert.Empty(await PhotoUrlsInDbAsync(property.Id));
    }

    [PostgresFact]
    public async Task Upload_OverTheGalleryLimit_Returns422AndKeepsTheGallery()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        for (var batch = 0; batch < PropertyPhotoLimits.MaxPhotos / PropertyPhotoLimits.MaxFilesPerRequest; batch++)
        {
            await UploadAsync(
                owner,
                property.Id,
                Enumerable.Range(0, PropertyPhotoLimits.MaxFilesPerRequest).Select(i => ($"f{batch}-{i}.png", "image/png", PngBytes)).ToArray());
        }

        var response = await owner.PostAsync($"/api/properties/{property.Id}/images", Form(("una-di-troppo.png", "image/png", PngBytes)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("property_photo_limit_reached", await CodeOfAsync(response));
        Assert.Equal(PropertyPhotoLimits.MaxPhotos, (await PhotoUrlsInDbAsync(property.Id)).Count);
        Assert.Equal(PropertyPhotoLimits.MaxPhotos, (await StoredPhotosOfAsync(property.Id)).Count);
    }

    [PostgresFact]
    public async Task Upload_MoreFilesThanOneRequestAccepts_Returns422()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var files = Enumerable.Range(0, PropertyPhotoLimits.MaxFilesPerRequest + 1).Select(i => ($"f{i}.png", "image/png", PngBytes)).ToArray();

        var response = await owner.PostAsync($"/api/properties/{property.Id}/images", Form(files));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("property_photo_too_many_files", await CodeOfAsync(response));
        Assert.Empty(await StoredPhotosOfAsync(property.Id));
    }

    [PostgresFact]
    public async Task Delete_PhotoNoLongerInTheGallery_Returns404WithStableCode()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var url = (await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes)))[0];
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/properties/{property.Id}/images?url={Uri.EscapeDataString(url)}")).StatusCode);

        var again = await owner.DeleteAsync($"/api/properties/{property.Id}/images?url={Uri.EscapeDataString(url)}");

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("property_photo_not_found", await CodeOfAsync(again));
    }

    [PostgresFact]
    public async Task Delete_WithoutTheUrl_Returns400()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);

        var response = await owner.DeleteAsync($"/api/properties/{property.Id}/images");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresFact]
    public async Task Reorder_StaleList_Returns409AndKeepsTheGallery()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var gallery = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes), ("b.png", "image/png", PngBytes));

        // A page that still shows one photo only (the second was uploaded from another tab).
        var response = await owner.PutAsJsonAsync($"/api/properties/{property.Id}/images/order", new[] { gallery[0] });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("property_photos_changed", await CodeOfAsync(response));
        Assert.Equal(gallery, await PhotoUrlsInDbAsync(property.Id));
    }

    [PostgresFact]
    public async Task Gallery_UserOfAnotherOrg_Gets404OnEveryActionAndTheObjectSurvives()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var gallery = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes), ("b.png", "image/png", PngBytes));
        var attackerId = NewUser();
        await _factory.SeedOrgForOwnerAsync(attackerId);
        using var attacker = Host(attackerId);
        var photos = $"/api/properties/{property.Id}/images";

        // The tenant filter hides the other org's property: the caller cannot learn that it exists.
        Assert.Equal(HttpStatusCode.NotFound, (await attacker.GetAsync(photos)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await attacker.PostAsync(photos, Form(("x.png", "image/png", PngBytes)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await attacker.DeleteAsync($"{photos}?url={Uri.EscapeDataString(gallery[0])}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await attacker.PutAsJsonAsync($"{photos}/order", new[] { gallery[1], gallery[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await attacker.PutAsJsonAsync($"{photos}/cover", new { url = gallery[1] })).StatusCode);

        Assert.Equal(gallery, await PhotoUrlsInDbAsync(property.Id));
        Assert.All(gallery, url => Assert.True(File.Exists(StoragePath(KeyOf(url)))));
    }

    [PostgresFact]
    public async Task Gallery_ColleagueOfTheSameOrgWithoutOwnership_Gets403OnWrites()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var gallery = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes));
        var colleagueId = await SeedColleagueAsync(property.OrgId);
        using var colleague = Host(colleagueId);
        var photos = $"/api/properties/{property.Id}/images";

        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.GetAsync(photos)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PostAsync(photos, Form(("x.png", "image/png", PngBytes)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.DeleteAsync($"{photos}?url={Uri.EscapeDataString(gallery[0])}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PutAsJsonAsync($"{photos}/order", gallery)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PutAsJsonAsync($"{photos}/cover", new { url = gallery[0] })).StatusCode);
        Assert.Equal(gallery, await PhotoUrlsInDbAsync(property.Id));
    }

    [PostgresFact]
    public async Task Gallery_PropertyManagerOfTheOrg_CanManageAColleaguesPhotos()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        var managerId = await SeedColleagueAsync(property.OrgId);
        using var manager = _factory.CreateAuthenticatedClient(managerId, "PropertyOwner,PropertyManager");

        var gallery = await UploadAsync(manager, property.Id, ("a.png", "image/png", PngBytes));

        Assert.Single(gallery);
    }

    [PostgresFact]
    public async Task Gallery_Anonymous_Returns401()
    {
        var property = await _factory.SeedPropertyAsync(NewUser());
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/properties/{property.Id}/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/properties/{property.Id}/images", Form(("x.png", "image/png", PngBytes)))).StatusCode);
    }

    [PostgresFact]
    public async Task Upload_ParallelRequestsOnTheSameProperty_KeepEveryPhoto()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            owner.PostAsync($"/api/properties/{property.Id}/images", Form(($"p{i}.png", "image/png", PngBytes)))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var saved = await PhotoUrlsInDbAsync(property.Id);
        Assert.Equal(8, saved.Count);
        Assert.Equal(8, saved.Distinct().Count());
        Assert.Equal(8, (await StoredPhotosOfAsync(property.Id)).Count);
    }

    [PostgresFact]
    public async Task UploadAndDelete_InParallel_NeitherOverwritesTheOther()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var existing = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes), ("b.png", "image/png", PngBytes));

        var delete = owner.DeleteAsync($"/api/properties/{property.Id}/images?url={Uri.EscapeDataString(existing[0])}");
        var uploads = Enumerable.Range(0, 4).Select(i =>
            owner.PostAsync($"/api/properties/{property.Id}/images", Form(($"n{i}.png", "image/png", PngBytes)))).ToList();
        await Task.WhenAll(uploads.Append(delete));

        Assert.Equal(HttpStatusCode.OK, (await delete).StatusCode);
        var saved = await PhotoUrlsInDbAsync(property.Id);
        Assert.Equal(5, saved.Count);
        Assert.DoesNotContain(existing[0], saved);
        Assert.Contains(existing[1], saved);
        Assert.Equal(5, (await StoredPhotosOfAsync(property.Id)).Count);
    }

    [PostgresFact]
    public async Task UpdateProperty_WithACopyReadBeforeAPhotoWasAdded_DoesNotPutBackTheOldPhotoList()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);

        // The edit form loads the row (tracked) ...
        using var editScope = _factory.Services.CreateScope();
        var repository = editScope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        var staleCopy = await repository.GetRecordAsync(property.Id);
        Assert.NotNull(staleCopy);
        Assert.Empty(staleCopy!.PhotoUrls);

        // ... a photo is uploaded meanwhile ...
        var gallery = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes));

        // ... and the form saves the other fields from the older copy.
        staleCopy.Name = "Nome nuovo";
        await repository.UpdateAsync(staleCopy);

        Assert.Equal(gallery, await PhotoUrlsInDbAsync(property.Id));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Nome nuovo", (await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id)).Name);
    }

    [PostgresFact]
    public async Task UpdateProperty_WithPhotoUrlsInTheBody_IgnoresThem()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = Host(ownerId);
        var gallery = await UploadAsync(owner, property.Id, ("a.png", "image/png", PngBytes));

        var response = await owner.PutAsJsonAsync($"/api/properties/{property.Id}", new { name = "Altro nome", photoUrls = new[] { "https://evil.example/x.jpg" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(gallery, await PhotoUrlsInDbAsync(property.Id));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient Host(string userId) => _factory.CreateAuthenticatedClient(userId, "PropertyOwner");

    private async Task<Property> SeedPublishedPropertyAsync(string ownerId)
    {
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id);
        row.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return property;
    }

    private static MultipartFormDataContent Form(params (string FileName, string ContentType, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (fileName, contentType, bytes) in files)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(content, "images", fileName);
        }

        return form;
    }

    private static async Task<IReadOnlyList<string>> UploadAsync(
        HttpClient client, Guid propertyId, params (string FileName, string ContentType, byte[] Bytes)[] files)
    {
        using var form = Form(files);
        var response = await client.PostAsync($"/api/properties/{propertyId}/images", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadPhotosAsync(response);
    }

    private static async Task<IReadOnlyList<string>> ReadPhotosAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("photoUrls").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The photos the anonymous property page lists, in the order the public pages show them.</summary>
    private async Task<IReadOnlyList<string>> PublicPhotosAsync(Guid propertyId)
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/properties/{propertyId}/public");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("photoUrls").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private async Task<IReadOnlyList<string>> PhotoUrlsInDbAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId)).PhotoUrls;
    }

    private Task<IReadOnlyList<string>> StoredPhotosOfAsync(Guid propertyId)
    {
        var folder = Path.Combine(_factory.StorageRoot, "public", "properties", propertyId.ToString(), "photos");
        IReadOnlyList<string> files = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
        return Task.FromResult(files);
    }

    private static string KeyOf(string url)
    {
        const string marker = "/storage/public/";
        return url[(url.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
    }

    private string StoragePath(string key) => Path.Combine(_factory.StorageRoot, "public", key.Replace('/', Path.DirectorySeparatorChar));

    private async Task<string> SeedColleagueAsync(Guid? orgId)
    {
        var colleagueId = $"auth0|photos-colleague-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = colleagueId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        // Onboarded for the org (PL-02): the 403 of these tests is about ownership, not about the onboarding gate.
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId!.Value, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
        return colleagueId;
    }
}
