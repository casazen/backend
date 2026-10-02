using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="PropertyPhotoService"/> (PC-04, A2-26) on the filesystem storage and an in-memory database: validation of
/// the files (type on the content, size, count), all-or-none uploads, order and cover, deletion that removes the object,
/// the storage folder of the property as the only thing a gallery can delete, and the org filter. The concurrency of
/// parallel changes (advisory lock) is covered on PostgreSQL by <c>PropertyPhotosPostgresTests</c>.
/// </summary>
public class PropertyPhotoServiceTests : IDisposable
{
    private const string PublicBaseUrl = "https://storage.test/public";

    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] WebpBytes =
        [0x52, 0x49, 0x46, 0x46, 0x1A, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x4C, 0x0D, 0x00, 0x00, 0x00];

    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherOrgId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "casazen-test-photos", Guid.NewGuid().ToString("N"));
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly IOptions<StorageOptions> _options;
    private readonly FileSystemFileStorage _storage;
    private readonly HookedStorage _hookedStorage;
    private readonly ImageStorageService _images;

    public PropertyPhotoServiceTests()
    {
        _options = Options.Create(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            PublicBaseUrl = PublicBaseUrl,
            FileSystem = new FileSystemStorageOptions { RootPath = _root },
        });
        _storage = new FileSystemFileStorage(_options, NullLogger<FileSystemFileStorage>.Instance);
        _hookedStorage = new HookedStorage(_storage);
        _images = new ImageStorageService(_hookedStorage, _options, NullLogger<ImageStorageService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ─── AddAsync ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_WithValidImages_StoresObjectsInThePublicBucketAndAppendsTheirUrlsInOrder()
    {
        var property = await SeedPropertyAsync();
        var service = CreateService();

        var gallery = await service.AddAsync(
            property.Id,
            [Image("facciata.JPG", "image/jpeg", JpegBytes), Image("salotto.png", "image/png", PngBytes), Image("bagno.webp", "image/webp", WebpBytes)]);

        Assert.Equal(3, gallery.Count);
        Assert.EndsWith(".jpg", gallery[0]);
        Assert.EndsWith(".png", gallery[1]);
        Assert.EndsWith(".webp", gallery[2]);
        foreach (var url in gallery)
        {
            Assert.StartsWith($"{PublicBaseUrl}/properties/{property.Id}/photos/", url);
            var key = _storage.TryGetPublicKey(url);
            Assert.NotNull(key);
            Assert.True(await _storage.ExistsAsync(StorageBucket.Public, key!));
            Assert.False(await _storage.ExistsAsync(StorageBucket.Private, key!));
        }

        Assert.Equal(gallery, (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task AddAsync_WithAnExistingGallery_KeepsTheExistingPhotosFirst()
    {
        var existing = new[] { $"{PublicBaseUrl}/properties/x/photos/old-1.jpg", $"{PublicBaseUrl}/properties/x/photos/old-2.jpg" };
        var property = await SeedPropertyAsync(existing);
        var service = CreateService();

        var gallery = await service.AddAsync(property.Id, [Image("nuova.png", "image/png", PngBytes)]);

        Assert.Equal(3, gallery.Count);
        Assert.Equal(existing, gallery.Take(2));
        Assert.EndsWith(".png", gallery[2]);
    }

    [Fact]
    public async Task AddAsync_WithNoFile_ThrowsPhotoNoneAndChangesNothing()
    {
        var property = await SeedPropertyAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService().AddAsync(property.Id, []));

        Assert.Equal(PropertyPhotoLimits.NoFileCode, ex.Code);
    }

    [Fact]
    public async Task AddAsync_MoreFilesThanOneRequestAccepts_ThrowsAndStoresNothing()
    {
        var property = await SeedPropertyAsync();
        var files = Enumerable.Range(0, PropertyPhotoLimits.MaxFilesPerRequest + 1)
            .Select(i => Image($"foto-{i}.png", "image/png", PngBytes))
            .ToList();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService().AddAsync(property.Id, files));

        Assert.Equal(PropertyPhotoLimits.TooManyFilesCode, ex.Code);
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task AddAsync_TextRenamedToJpg_IsRejectedOnItsContentAndStoresNothing()
    {
        var property = await SeedPropertyAsync();
        var script = "<script>alert(1)</script>"u8.ToArray();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService().AddAsync(property.Id, [Image("trucco.jpg", "image/jpeg", script)]));

        Assert.Equal(PropertyPhotoLimits.InvalidTypeCode, ex.Code);
        Assert.Empty(StoredObjects());
        Assert.Empty((await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task AddAsync_PngContentDeclaredAsJpeg_IsRejected()
    {
        var property = await SeedPropertyAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService().AddAsync(property.Id, [Image("foto.jpg", "image/jpeg", PngBytes)]));

        Assert.Equal(PropertyPhotoLimits.InvalidTypeCode, ex.Code);
    }

    [Fact]
    public async Task AddAsync_ExtensionThatDoesNotMatchTheContent_IsRejected()
    {
        var property = await SeedPropertyAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService().AddAsync(property.Id, [Image("foto.png", "image/jpeg", JpegBytes)]));

        Assert.Equal(PropertyPhotoLimits.InvalidTypeCode, ex.Code);
    }

    [Theory]
    [InlineData("scheda.pdf", "application/pdf")]
    [InlineData("foto.gif", "image/gif")]
    [InlineData("foto.svg", "image/svg+xml")]
    public async Task AddAsync_FormatsOutsideJpegPngWebp_AreRejected(string fileName, string contentType)
    {
        var property = await SeedPropertyAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService().AddAsync(property.Id, [Image(fileName, contentType, PngBytes)]));

        Assert.Equal(PropertyPhotoLimits.InvalidTypeCode, ex.Code);
    }

    [Fact]
    public async Task AddAsync_FileOverTheSizeLimit_IsRejected()
    {
        var property = await SeedPropertyAsync();
        var huge = new Mock<IFormFile>();
        huge.Setup(f => f.FileName).Returns("enorme.jpg");
        huge.Setup(f => f.ContentType).Returns("image/jpeg");
        huge.Setup(f => f.Length).Returns(PropertyPhotoLimits.MaxFileSizeBytes + 1);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService().AddAsync(property.Id, [huge.Object]));

        Assert.Equal(PropertyPhotoLimits.InvalidSizeCode, ex.Code);
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task AddAsync_EmptyFile_IsRejected()
    {
        var property = await SeedPropertyAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService().AddAsync(property.Id, [Image("vuota.png", "image/png", [])]));

        Assert.Equal(PropertyPhotoLimits.InvalidSizeCode, ex.Code);
    }

    [Fact]
    public async Task AddAsync_OneInvalidFileInTheBatch_StoresNoneOfThem()
    {
        var property = await SeedPropertyAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => CreateService().AddAsync(
            property.Id,
            [Image("ok.png", "image/png", PngBytes), Image("cattivo.jpg", "image/jpeg", "MZ"u8.ToArray())]));

        Assert.Empty(StoredObjects());
        Assert.Empty((await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task AddAsync_ExceedingTheGalleryLimit_ThrowsAndStoresNothing()
    {
        var full = Enumerable.Range(1, PropertyPhotoLimits.MaxPhotos - 1).Select(i => $"{PublicBaseUrl}/properties/x/photos/{i}.jpg").ToArray();
        var property = await SeedPropertyAsync(full);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService().AddAsync(
            property.Id, [Image("a.png", "image/png", PngBytes), Image("b.png", "image/png", PngBytes)]));

        Assert.Equal(PropertyPhotoLimits.LimitReachedCode, ex.Code);
        Assert.Equal(new object[] { PropertyPhotoLimits.MaxPhotos, PropertyPhotoLimits.MaxPhotos - 1 }, ex.MessageArgs);
        Assert.Empty(StoredObjects());
        Assert.Equal(full, (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task AddAsync_FillingTheGalleryExactly_Succeeds()
    {
        var almost = Enumerable.Range(1, PropertyPhotoLimits.MaxPhotos - 1).Select(i => $"{PublicBaseUrl}/properties/x/photos/{i}.jpg").ToArray();
        var property = await SeedPropertyAsync(almost);

        var gallery = await CreateService().AddAsync(property.Id, [Image("ultima.png", "image/png", PngBytes)]);

        Assert.Equal(PropertyPhotoLimits.MaxPhotos, gallery.Count);
    }

    [Fact]
    public async Task AddAsync_WhenAnotherUploadFillsTheGalleryMeanwhile_RemovesTheObjectsItStored()
    {
        var property = await SeedPropertyAsync();
        var service = CreateService();
        // A parallel upload commits its photos while this one is storing its files.
        _hookedStorage.OnPut = () =>
        {
            using var other = CreateDb();
            var row = other.Properties.Single(p => p.Id == property.Id);
            row.PhotoUrls = [.. Enumerable.Range(1, PropertyPhotoLimits.MaxPhotos).Select(i => $"{PublicBaseUrl}/properties/x/photos/{i}.jpg")];
            other.SaveChanges();
            _hookedStorage.OnPut = null;
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.AddAsync(property.Id, [Image("tardiva.png", "image/png", PngBytes)]));

        Assert.Equal(PropertyPhotoLimits.LimitReachedCode, ex.Code);
        Assert.Empty(StoredObjects());
        Assert.Equal(PropertyPhotoLimits.MaxPhotos, (await ReloadAsync(property.Id)).PhotoUrls.Count);
    }

    [Fact]
    public async Task AddAsync_WhenTheRowWasAlreadyReadInTheSameContext_UsesTheCommittedPhotoList()
    {
        var property = await SeedPropertyAsync();
        await using var db = CreateDb();
        var service = CreateService(db);
        // The request first reads the row (the host permission check) ...
        var tracked = await db.Properties.SingleAsync(p => p.Id == property.Id);
        Assert.Empty(tracked.PhotoUrls);
        // ... and meanwhile another request adds a photo.
        await using (var other = CreateDb())
        {
            other.Properties.Single(p => p.Id == property.Id).PhotoUrls = [$"{PublicBaseUrl}/properties/x/photos/other.jpg"];
            await other.SaveChangesAsync();
        }

        var gallery = await service.AddAsync(property.Id, [Image("mia.png", "image/png", PngBytes)]);

        Assert.Equal(2, gallery.Count);
        Assert.Equal($"{PublicBaseUrl}/properties/x/photos/other.jpg", gallery[0]);
    }

    [Fact]
    public async Task AddAsync_PropertyOfAnotherOrg_ThrowsNotFoundAndStoresNothing()
    {
        var property = await SeedPropertyAsync();
        await using var db = CreateDb(OtherOrgId);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService(db).AddAsync(property.Id, [Image("intruso.png", "image/png", PngBytes)]));

        Assert.Empty(StoredObjects());
        Assert.Empty((await ReloadAsync(property.Id)).PhotoUrls);
    }

    // ─── DeleteAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_OwnPhoto_RemovesTheEntryAndTheStorageObject()
    {
        var property = await SeedPropertyAsync();
        var gallery = await CreateService().AddAsync(
            property.Id, [Image("a.png", "image/png", PngBytes), Image("b.png", "image/png", PngBytes)]);
        var keyOfFirst = _storage.TryGetPublicKey(gallery[0])!;

        var after = await CreateService().DeleteAsync(property.Id, gallery[0]);

        Assert.Equal([gallery[1]], after);
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, keyOfFirst));
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, _storage.TryGetPublicKey(gallery[1])!));
        Assert.Equal([gallery[1]], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task DeleteAsync_TheCover_MakesTheNextPhotoTheCover()
    {
        var property = await SeedPropertyAsync();
        var gallery = await CreateService().AddAsync(
            property.Id,
            [Image("a.png", "image/png", PngBytes), Image("b.png", "image/png", PngBytes), Image("c.png", "image/png", PngBytes)]);

        var after = await CreateService().DeleteAsync(property.Id, gallery[0]);

        Assert.Equal([gallery[1], gallery[2]], after);
    }

    [Fact]
    public async Task DeleteAsync_PhotoNotInTheGallery_ThrowsNotFoundAndKeepsTheStorageUntouched()
    {
        var property = await SeedPropertyAsync();
        var gallery = await CreateService().AddAsync(property.Id, [Image("a.png", "image/png", PngBytes)]);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(property.Id, $"{PublicBaseUrl}/properties/{property.Id}/photos/inventata.png"));

        Assert.Equal(PropertyPhotoLimits.NotFoundCode, ex.Code);
        Assert.Single(StoredObjects());
        Assert.Equal(gallery, (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task DeleteAsync_EntryThatIsAnotherPropertysObject_IsRemovedFromTheListButNeverFromTheStorage()
    {
        // A gallery entry pointing at somebody else's photo (legacy data, a hand-edited row) must not let this
        // property's host delete that object from the shared public bucket.
        var victim = await SeedPropertyAsync();
        var victimUrl = (await CreateService().AddAsync(victim.Id, [Image("vittima.png", "image/png", PngBytes)]))[0];
        var attacker = await SeedPropertyAsync([victimUrl]);

        var after = await CreateService().DeleteAsync(attacker.Id, victimUrl);

        Assert.Empty(after);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, _storage.TryGetPublicKey(victimUrl)!));
    }

    [Theory]
    [InlineData("/uploads/properties/p/old.jpg")]
    [InlineData("https://cdn.example/external.jpg")]
    public async Task DeleteAsync_LegacyOrExternalEntry_IsRemovedFromTheListOnly(string entry)
    {
        var property = await SeedPropertyAsync([entry]);

        var after = await CreateService().DeleteAsync(property.Id, entry);

        Assert.Empty(after);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheStorageFails_KeepsThePhotoInTheGalleryForARetry()
    {
        var property = await SeedPropertyAsync();
        var gallery = await CreateService().AddAsync(property.Id, [Image("a.png", "image/png", PngBytes)]);
        _hookedStorage.FailDeletes = true;

        await Assert.ThrowsAsync<IOException>(() => CreateService().DeleteAsync(property.Id, gallery[0]));

        Assert.Equal(gallery, (await ReloadAsync(property.Id)).PhotoUrls);
        _hookedStorage.FailDeletes = false;
        Assert.Empty(await CreateService().DeleteAsync(property.Id, gallery[0]));
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task DeleteAsync_PropertyOfAnotherOrg_ThrowsNotFoundAndKeepsTheObject()
    {
        var property = await SeedPropertyAsync();
        var gallery = await CreateService().AddAsync(property.Id, [Image("a.png", "image/png", PngBytes)]);
        await using var db = CreateDb(OtherOrgId);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).DeleteAsync(property.Id, gallery[0]));

        Assert.Single(StoredObjects());
        Assert.Equal(gallery, (await ReloadAsync(property.Id)).PhotoUrls);
    }

    // ─── ReorderAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReorderAsync_PermutationOfTheGallery_SavesTheNewOrderAndTheFirstIsTheCover()
    {
        var property = await SeedPropertyAsync(["a", "b", "c"]);

        var after = await CreateService().ReorderAsync(property.Id, ["c", "a", "b"]);

        Assert.Equal(["c", "a", "b"], after);
        Assert.Equal(["c", "a", "b"], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Theory]
    [InlineData("a", "b")] // a photo missing: the page is stale (one was added elsewhere)
    [InlineData("a", "b", "c", "d")] // an unknown photo
    [InlineData("a", "b", "b")] // a photo twice, another dropped
    [InlineData("a", "b", "x")] // a photo replaced by an unknown one
    public async Task ReorderAsync_ListThatIsNotTheCurrentGallery_ThrowsConflictAndKeepsTheOrder(params string[] sent)
    {
        var property = await SeedPropertyAsync(["a", "b", "c"]);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => CreateService().ReorderAsync(property.Id, sent));

        Assert.Equal(PropertyPhotoLimits.GalleryChangedCode, ex.Code);
        Assert.Equal(["a", "b", "c"], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task ReorderAsync_PropertyOfAnotherOrg_ThrowsNotFound()
    {
        var property = await SeedPropertyAsync(["a", "b"]);
        await using var db = CreateDb(OtherOrgId);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).ReorderAsync(property.Id, ["b", "a"]));

        Assert.Equal(["a", "b"], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    // ─── SetCoverAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetCoverAsync_LaterPhoto_MovesFirstAndKeepsTheOthersInOrder()
    {
        var property = await SeedPropertyAsync(["a", "b", "c", "d"]);

        var after = await CreateService().SetCoverAsync(property.Id, "c");

        Assert.Equal(["c", "a", "b", "d"], after);
        Assert.Equal(["c", "a", "b", "d"], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task SetCoverAsync_PhotoThatIsAlreadyTheCover_ChangesNothing()
    {
        var property = await SeedPropertyAsync(["a", "b"]);

        var after = await CreateService().SetCoverAsync(property.Id, "a");

        Assert.Equal(["a", "b"], after);
    }

    [Fact]
    public async Task SetCoverAsync_PhotoNotInTheGallery_ThrowsNotFound()
    {
        var property = await SeedPropertyAsync(["a", "b"]);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateService().SetCoverAsync(property.Id, "z"));

        Assert.Equal(PropertyPhotoLimits.NotFoundCode, ex.Code);
        Assert.Equal(["a", "b"], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    [Fact]
    public async Task SetCoverAsync_PropertyOfAnotherOrg_ThrowsNotFound()
    {
        var property = await SeedPropertyAsync(["a", "b"]);
        await using var db = CreateDb(OtherOrgId);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).SetCoverAsync(property.Id, "b"));

        Assert.Equal(["a", "b"], (await ReloadAsync(property.Id)).PhotoUrls);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private AppDbContext CreateDb(Guid? tenantOrgId = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options,
            tenantOrgId is { } orgId ? new FixedTenant(orgId) : null);

    private PropertyPhotoService CreateService(AppDbContext? db = null) =>
        new(db ?? CreateDb(OrgId), _hookedStorage, _images, NullLogger<PropertyPhotoService>.Instance);

    private async Task<Property> SeedPropertyAsync(IEnumerable<string>? photoUrls = null)
    {
        await using var db = CreateDb();
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            OwnerId = "auth0|owner",
            Name = "Casa",
            Address = "Via Roma 1",
            City = "Rimini",
            PhotoUrls = [.. photoUrls ?? []],
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task<Property> ReloadAsync(Guid propertyId)
    {
        await using var db = CreateDb();
        return await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private string[] StoredObjects() =>
        Directory.Exists(_storage.BucketRoot(StorageBucket.Public))
            ? Directory.GetFiles(_storage.BucketRoot(StorageBucket.Public), "*", SearchOption.AllDirectories)
            : [];

    private static IFormFile Image(string fileName, string contentType, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "images", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;
        public bool FilterEnabled => true;
    }

    /// <summary>The filesystem storage with a hook on each upload and a switch that fails the deletions.</summary>
    private sealed class HookedStorage(IFileStorage inner) : IFileStorage
    {
        public Action? OnPut { get; set; }

        public bool FailDeletes { get; set; }

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            await inner.PutAsync(bucket, key, content, contentType, cancellationToken);
            OnPut?.Invoke();
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(bucket, key, cancellationToken);

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            FailDeletes ? throw new IOException("storage unavailable") : inner.DeleteAsync(bucket, key, cancellationToken);

        public string GetPublicUrl(string key) => inner.GetPublicUrl(key);

        public string? TryGetPublicKey(string url) => inner.TryGetPublicKey(url);

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            inner.GetSignedReadUrlAsync(key, lifetime, downloadFileName, cancellationToken);
    }
}
