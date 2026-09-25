using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-07 (A2-03, A2-31, A9-04) over HTTP on PostgreSQL: property documents live in the private
/// bucket and are readable only through the authenticated download endpoint (tenant filter +
/// ownership); photos get absolute public URLs; nothing is served from the legacy /uploads folder.
/// The Testing host uses the filesystem provider in a per-factory temp folder.
/// </summary>
public class FileStorageIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ApiBaseUrl = "https://casazen-api-test.up.railway.app";
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj <<>> endobj\n%%EOF");
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CasazenWebApplicationFactory _factory;

    public FileStorageIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewOwner() => $"auth0|storage-owner-{Guid.NewGuid():N}";

    [PostgresFact]
    public async Task DownloadDocument_AsOwner_ReturnsStoredBytesAsPrivateAttachment()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");

        var (documentId, downloadUrl) = await UploadDocumentAsync(owner, property.Id, "Certificato CIN.pdf");

        Assert.Equal($"/api/properties/{property.Id}/documents/{documentId}/download", downloadUrl);
        var stored = await GetStoredDocumentAsync(documentId);
        Assert.StartsWith($"properties/{property.Id}/documents/", stored.StorageUrl);
        Assert.True(File.Exists(Path.Combine(_factory.StorageRoot, "private", stored.StorageUrl)));

        var response = await owner.GetAsync(downloadUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PdfBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Certificato CIN.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [PostgresFact]
    public async Task DownloadDocument_UserOfAnotherOrg_IsDeniedWithoutFileContent()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var (_, downloadUrl) = await UploadDocumentAsync(owner, property.Id, "contratto.pdf");

        var otherOrgUser = NewOwner();
        await _factory.SeedOrgForOwnerAsync(otherOrgUser);
        using var attacker = _factory.CreateAuthenticatedClient(otherOrgUser, "PropertyOwner");

        var response = await attacker.GetAsync(downloadUrl);

        // Cross-tenant: the tenant query filter hides the other org's property (404, as AC7 of US-004),
        // so the caller cannot even learn that the document exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(PdfBytes, await response.Content.ReadAsByteArrayAsync());
        var signed = await attacker.GetAsync(downloadUrl.Replace("/download", "/signed-url"));
        Assert.Equal(HttpStatusCode.NotFound, signed.StatusCode);
    }

    [PostgresFact]
    public async Task DownloadDocument_SameOrgUserNotOwner_Returns403()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var (_, downloadUrl) = await UploadDocumentAsync(owner, property.Id, "ape.pdf");
        var colleagueId = await SeedColleagueAsync(property.OrgId);
        using var colleague = _factory.CreateAuthenticatedClient(colleagueId, "PropertyOwner");

        var response = await colleague.GetAsync(downloadUrl);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(PdfBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task DownloadDocument_Anonymous_Returns401()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var (_, downloadUrl) = await UploadDocumentAsync(owner, property.Id, "doc.pdf");
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(downloadUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task GetDocumentSignedUrl_WithFileSystemProvider_Returns501()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var (documentId, _) = await UploadDocumentAsync(owner, property.Id, "doc.pdf");

        var response = await owner.GetAsync($"/api/properties/{property.Id}/documents/{documentId}/signed-url");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("signed_url_unavailable", await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task DeleteDocument_RemovesStoredFile()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var (documentId, _) = await UploadDocumentAsync(owner, property.Id, "doc.pdf");
        var stored = await GetStoredDocumentAsync(documentId);

        var response = await owner.DeleteAsync($"/api/properties/{property.Id}/documents/{documentId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_factory.StorageRoot, "private", stored.StorageUrl)));
    }

    [PostgresFact]
    public async Task UploadImages_ReturnsAbsolutePublicUrlThatIsServed()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(PngBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "images", "facciata.png");

        var upload = await owner.PostAsync($"/api/properties/{property.Id}/images", form);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var url = body.RootElement.GetProperty("uploadedImages")[0].GetString()!;
        Assert.StartsWith($"{ApiBaseUrl}/storage/public/properties/{property.Id}/photos/", url);
        Assert.EndsWith(".png", url);

        using var anonymous = _factory.CreateClient();
        var photo = await anonymous.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(PngBytes, await photo.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task PrivateFiles_AreNeverServedAsStaticFiles()
    {
        var ownerId = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var (documentId, _) = await UploadDocumentAsync(owner, property.Id, "doc.pdf");
        var key = (await GetStoredDocumentAsync(documentId)).StorageUrl;
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/storage/public/{key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/storage/private/{key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/storage/public/%2e%2e/private/{key}")).StatusCode);
    }

    [PostgresFact]
    public async Task LegacyUploadsFolder_IsNeverServed()
    {
        // A2-31: files left in wwwroot/uploads by the old storage (environments with a volume) used to be
        // downloadable anonymously with permanent URLs.
        var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var uploadsRoot = Path.Combine(webRoot, "uploads");
        var uploadsExisted = Directory.Exists(uploadsRoot);
        var folder = Path.Combine(uploadsRoot, "properties", Guid.NewGuid().ToString(), "documents");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "legacy.pdf"), PdfBytes);
        try
        {
            using var anonymous = _factory.CreateClient();
            var relative = Path.GetRelativePath(webRoot, Path.Combine(folder, "legacy.pdf")).Replace('\\', '/');

            var response = await anonymous.GetAsync($"/{relative}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Directory.Delete(uploadsExisted ? Path.GetDirectoryName(folder)! : uploadsRoot, recursive: true);
        }
    }

    private static async Task<(Guid DocumentId, string DownloadUrl)> UploadDocumentAsync(
        HttpClient client, Guid propertyId, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(PdfBytes);
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", fileName);
        form.Add(new StringContent("CinCertificate"), "documentType");

        var response = await client.PostAsync($"/api/properties/{propertyId}/documents", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("id").GetGuid(), body.RootElement.GetProperty("downloadUrl").GetString()!);
    }

    private async Task<PropertyDocument> GetStoredDocumentAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PropertyDocuments.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId);
    }

    private async Task<string> SeedColleagueAsync(Guid? orgId)
    {
        var colleagueId = $"auth0|storage-colleague-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = colleagueId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = orgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return colleagueId;
    }
}
