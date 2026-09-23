using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Storage;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-07: <c>storage:migrate-legacy</c> copies the files of the old local-disk storage into the object
/// storage and rewrites the database references, and can be re-run safely.
/// </summary>
public sealed class LegacyFileMigrationServiceTests : IAsyncLifetime
{
    private const string PublicBaseUrl = "https://storage.test/public";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "casazen-legacy-migration", Guid.NewGuid().ToString("N"));
    private PostgresTestDatabase? _database;
    private FileSystemFileStorage _storage = null!;

    private string ImageRoot => Path.Combine(_workDir, "legacy", "wwwroot", "uploads", "properties");
    private string GuestRoot => Path.Combine(_workDir, "legacy", "uploads", "guest-documents");

    public async Task InitializeAsync()
    {
        _storage = new FileSystemFileStorage(
            Options.Create(new StorageOptions
            {
                Provider = StorageOptions.FileSystemProvider,
                PublicBaseUrl = PublicBaseUrl,
                FileSystem = new FileSystemStorageOptions { RootPath = Path.Combine(_workDir, "bucket") },
            }),
            NullLogger<FileSystemFileStorage>.Instance);

        if (PostgresTestServer.IsAvailable)
        {
            _database = await PostgresTestDatabase.CreateAsync("legacy");
            _database.MigrateToLatest();
        }
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    [PostgresFact]
    public async Task MigrateAsync_WithLegacyFilesOnDisk_UploadsThemAndRewritesReferences()
    {
        var seed = await SeedLegacyDataAsync();

        var report = await RunAsync(dryRun: false);

        Assert.Equal(4, report.Uploaded);
        Assert.Equal(0, report.AlreadyPresent);
        Assert.Equal(1, report.Missing);
        Assert.Equal(4, report.ReferencesRewritten);

        await using var db = _database!.CreateContext();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == seed.PropertyId);
        Assert.Equal($"{PublicBaseUrl}/properties/{seed.PropertyId}/photos/photo1.jpg", property.PhotoUrls[0]);
        Assert.Equal("https://cdn.example/external.jpg", property.PhotoUrls[1]);
        Assert.Equal($"/uploads/properties/{seed.PropertyId}/lost.jpg", property.PhotoUrls[2]);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, $"properties/{seed.PropertyId}/photos/photo1.jpg"));

        var document = await db.PropertyDocuments.SingleAsync(d => d.Id == seed.DocumentId);
        Assert.Equal($"properties/{seed.PropertyId}/documents/cin.pdf", document.StorageUrl);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Private, document.StorageUrl));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, document.StorageUrl));

        var guest = await db.Guests.IgnoreQueryFilters().SingleAsync(g => g.Id == seed.GuestId);
        Assert.Equal($"guest-documents/{seed.OrgId}/{seed.GuestId}/scan.png", guest.DocumentScanUrl);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Private, guest.DocumentScanUrl!));

        var supplier = await db.SupplierProfiles.SingleAsync(s => s.OrgId == seed.SupplierOrgId);
        var supplierUrls = JsonSerializer.Deserialize<List<string>>(supplier.PhotoUrlsJson)!;
        Assert.Equal([$"{PublicBaseUrl}/suppliers/{seed.SupplierOrgId}/photos/team.webp"], supplierUrls);
    }

    [PostgresFact]
    public async Task MigrateAsync_RunTwice_SecondRunChangesNothing()
    {
        var seed = await SeedLegacyDataAsync();
        await RunAsync(dryRun: false);
        var snapshot = await SnapshotReferencesAsync(seed);

        var second = await RunAsync(dryRun: false);

        Assert.Equal(0, second.Uploaded);
        Assert.Equal(0, second.ReferencesRewritten);
        Assert.Equal(1, second.Missing);
        Assert.Equal(snapshot, await SnapshotReferencesAsync(seed));
    }

    [PostgresFact]
    public async Task MigrateAsync_ObjectAlreadyInStorage_RewritesReferenceWithoutUploading()
    {
        // A previous run uploaded the file but crashed before saving the database.
        var seed = await SeedLegacyDataAsync();
        var key = $"properties/{seed.PropertyId}/documents/cin.pdf";
        await _storage.PutAsync(StorageBucket.Private, key, new MemoryStream([42]), "application/pdf");
        File.Delete(Path.Combine(ImageRoot, seed.PropertyId.ToString(), "documents", "cin.pdf"));

        var report = await RunAsync(dryRun: false);

        Assert.Equal(1, report.AlreadyPresent);
        await using var db = _database!.CreateContext();
        Assert.Equal(key, (await db.PropertyDocuments.SingleAsync(d => d.Id == seed.DocumentId)).StorageUrl);
    }

    [PostgresFact]
    public async Task MigrateAsync_DryRun_ChangesNeitherStorageNorDatabase()
    {
        var seed = await SeedLegacyDataAsync();
        var before = await SnapshotReferencesAsync(seed);

        var report = await RunAsync(dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(4, report.Uploaded);
        Assert.Equal(before, await SnapshotReferencesAsync(seed));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Private, $"properties/{seed.PropertyId}/documents/cin.pdf"));
    }

    private async Task<LegacyFileMigrationReport> RunAsync(bool dryRun)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ImageStorage:LocalPath"] = ImageRoot,
            ["ImageStorage:BaseUrl"] = "/uploads/properties",
            ["GuestDocumentStorage:LocalPath"] = GuestRoot,
            ["GuestDocumentStorage:BaseUrl"] = "/uploads/guest-documents",
        }).Build();
        await using var db = _database!.CreateContext();
        var service = new LegacyFileMigrationService(db, _storage, configuration, NullLogger<LegacyFileMigrationService>.Instance);
        return await service.MigrateAsync(dryRun);
    }

    private async Task<LegacySeed> SeedLegacyDataAsync()
    {
        var org = new OrgEntity { Name = "Legacy Org", Slug = $"legacy-{Guid.NewGuid():N}", DisplayName = "Legacy Org", ContactEmail = "o@example.com" };
        var supplierOrg = new OrgEntity { Name = "Supplier Org", Slug = $"legacy-sup-{Guid.NewGuid():N}", DisplayName = "Supplier", ContactEmail = "s@example.com" };
        var property = new Property
        {
            OwnerId = "auth0|legacy-owner",
            OrgId = org.Id,
            Name = "Legacy",
            Address = $"Via Vecchia {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
        };
        property.PhotoUrls =
        [
            $"/uploads/properties/{property.Id}/photo1.jpg",
            "https://cdn.example/external.jpg",
            $"/uploads/properties/{property.Id}/lost.jpg",
        ];
        var document = new PropertyDocument
        {
            PropertyId = property.Id,
            FileName = "Certificato CIN.pdf",
            StorageUrl = $"/uploads/properties/{property.Id}/documents/cin.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = "auth0|legacy-owner",
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"{Guid.NewGuid():N}@example.com",
        };
        guest.DocumentScanUrl = $"/uploads/guest-documents/{org.Id}/{guest.Id}/scan.png";
        var supplier = new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            LegalName = "Pulizie Srl",
            Phone = "+390000000",
            Email = "sup@example.com",
            PhotoUrlsJson = JsonSerializer.Serialize(new[] { $"/uploads/properties/{supplierOrg.Id}/team.webp" }),
        };

        await using (var db = _database!.CreateContext())
        {
            db.Orgs.AddRange(org, supplierOrg);
            db.Properties.Add(property);
            db.PropertyDocuments.Add(document);
            db.Guests.Add(guest);
            db.SupplierProfiles.Add(supplier);
            await db.SaveChangesAsync();
        }

        WriteLegacyFile(ImageRoot, property.Id.ToString(), "photo1.jpg");
        WriteLegacyFile(ImageRoot, property.Id.ToString(), "documents", "cin.pdf");
        WriteLegacyFile(GuestRoot, org.Id.ToString(), guest.Id.ToString(), "scan.png");
        WriteLegacyFile(ImageRoot, supplierOrg.Id.ToString(), "team.webp");

        return new LegacySeed(org.Id, property.Id, document.Id, guest.Id, supplierOrg.Id);
    }

    private async Task<string> SnapshotReferencesAsync(LegacySeed seed)
    {
        await using var db = _database!.CreateContext();
        var property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == seed.PropertyId);
        var document = await db.PropertyDocuments.AsNoTracking().SingleAsync(d => d.Id == seed.DocumentId);
        var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
        var supplier = await db.SupplierProfiles.AsNoTracking().SingleAsync(s => s.OrgId == seed.SupplierOrgId);
        return string.Join('|', [.. property.PhotoUrls, document.StorageUrl, guest.DocumentScanUrl, supplier.PhotoUrlsJson]);
    }

    private static void WriteLegacyFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    private sealed record LegacySeed(Guid OrgId, Guid PropertyId, Guid DocumentId, Guid GuestId, Guid SupplierOrgId);
}
