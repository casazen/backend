using Casazen.Core.Branding;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Casazen.Tests.Unit.Branding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="OrgBrandingService"/> (BK-12, A3-17) on the filesystem storage provider: images in the public bucket with
/// absolute URLs, validated from their bytes, previous image deleted, nothing changed on an invalid value.
/// </summary>
public class OrgBrandingServiceTests : IDisposable
{
    private const string PublicBaseUrl = "https://api.test/storage/public";

    private readonly string _root;
    private readonly FileSystemFileStorage _storage;
    private readonly AppDbContext _db;
    private readonly OrgBrandingService _service;
    private readonly OrgEntity _org;

    public OrgBrandingServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "casazen-test-storage", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            PublicBaseUrl = PublicBaseUrl,
            SignedUrlTtlMinutes = 5,
            FileSystem = new FileSystemStorageOptions { RootPath = _root },
        });
        _storage = new FileSystemFileStorage(options, NullLogger<FileSystemFileStorage>.Instance);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"org-branding-{Guid.NewGuid():N}")
            .Options);
        _org = new OrgEntity { Name = "Villa Parco", DisplayName = "Villa Parco", Slug = "villa-parco", ContactEmail = "host@example.com" };
        _db.Orgs.Add(_org);
        _db.SaveChanges();
        _service = new OrgBrandingService(_db, _storage, NullLogger<OrgBrandingService>.Instance);
    }

    [Fact]
    public async Task UpdateAsync_ValidValues_StoresNormalizedValues()
    {
        var org = await _service.UpdateAsync(_org.Id, new OrgBrandingUpdate("#1A6B8F", "Montagna", "  Baite\n in quota "));

        Assert.NotNull(org);
        var stored = await _db.Orgs.AsNoTracking().FirstAsync(o => o.Id == _org.Id);
        Assert.Equal("#1a6b8f", stored.ThemeColor);
        Assert.Equal("montagna", stored.PublicThemeId);
        Assert.Equal("Baite in quota", stored.Tagline);
    }

    [Fact]
    public async Task UpdateAsync_EmptyValues_ClearsColorThemeAndTagline()
    {
        await _service.UpdateAsync(_org.Id, new OrgBrandingUpdate("#123456", "urban", "Slogan"));

        await _service.UpdateAsync(_org.Id, new OrgBrandingUpdate("", null, " "));

        var stored = await _db.Orgs.AsNoTracking().FirstAsync(o => o.Id == _org.Id);
        Assert.Null(stored.ThemeColor);
        Assert.Null(stored.PublicThemeId);
        Assert.Null(stored.Tagline);
    }

    [Fact]
    public async Task UpdateAsync_InvalidColor_ThrowsAndChangesNothing()
    {
        await _service.UpdateAsync(_org.Id, new OrgBrandingUpdate("#123456", "urban", "Slogan"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.UpdateAsync(_org.Id, new OrgBrandingUpdate("red;}", "mare", "Altro")));

        Assert.Equal(OrgBrandingRules.ColorInvalidCode, ex.Code);
        var stored = await _db.Orgs.AsNoTracking().FirstAsync(o => o.Id == _org.Id);
        Assert.Equal("#123456", stored.ThemeColor);
        Assert.Equal("urban", stored.PublicThemeId);
        Assert.Equal("Slogan", stored.Tagline);
    }

    [Fact]
    public async Task UpdateAsync_UnknownOrg_ReturnsNull()
    {
        Assert.Null(await _service.UpdateAsync(Guid.NewGuid(), new OrgBrandingUpdate(null, null, null)));
    }

    [Fact]
    public async Task SetImageAsync_ValidLogo_StoresInPublicBucketAndSetsAbsoluteUrl()
    {
        var org = await _service.SetImageAsync(_org.Id, BrandingImageKind.Logo, new MemoryStream(TestImageBytes.Png(400, 120)));

        Assert.NotNull(org);
        Assert.StartsWith($"{PublicBaseUrl}/orgs/{_org.Id}/branding/logo/", org!.LogoUrl);
        Assert.EndsWith(".png", org.LogoUrl);
        var key = _storage.TryGetPublicKey(org.LogoUrl!);
        Assert.NotNull(key);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, key!));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Private, key!));
    }

    [Fact]
    public async Task SetImageAsync_ExtensionComesFromContentNotFromClient_JpegStoredAsJpg()
    {
        var org = await _service.SetImageAsync(_org.Id, BrandingImageKind.Hero, new MemoryStream(TestImageBytes.Jpeg(2400, 1000)));

        Assert.StartsWith($"{PublicBaseUrl}/orgs/{_org.Id}/branding/hero/", org!.HeroImageUrl);
        Assert.EndsWith(".jpg", org.HeroImageUrl);
    }

    [Fact]
    public async Task SetImageAsync_ReplacingLogo_DeletesPreviousObject()
    {
        var first = (await _service.SetImageAsync(_org.Id, BrandingImageKind.Logo, new MemoryStream(TestImageBytes.Png(400, 120))))!.LogoUrl!;
        var firstKey = _storage.TryGetPublicKey(first)!;

        var second = (await _service.SetImageAsync(_org.Id, BrandingImageKind.Logo, new MemoryStream(TestImageBytes.Png(300, 100))))!.LogoUrl!;

        Assert.NotEqual(first, second);
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, firstKey));
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, _storage.TryGetPublicKey(second)!));
    }

    [Fact]
    public async Task SetImageAsync_InvalidImage_ThrowsAndStoresNothing()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.SetImageAsync(_org.Id, BrandingImageKind.Logo, new MemoryStream(TestImageBytes.Svg())));

        Assert.Equal(OrgBrandingRules.ImageTypeInvalidCode, ex.Code);
        Assert.Null((await _db.Orgs.AsNoTracking().FirstAsync(o => o.Id == _org.Id)).LogoUrl);
        Assert.False(Directory.Exists(Path.Combine(_root, "public", "orgs")));
    }

    [Fact]
    public async Task SetImageAsync_HeroTooSmall_ThrowsDimensionsInvalid()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.SetImageAsync(_org.Id, BrandingImageKind.Hero, new MemoryStream(TestImageBytes.Png(400, 120))));

        Assert.Equal(OrgBrandingRules.ImageDimensionsInvalidCode, ex.Code);
    }

    [Fact]
    public async Task SetImageAsync_OverSizeLimit_ThrowsTooLargeWithoutReadingWholeStream()
    {
        var bytes = TestImageBytes.Png(400, 120, totalLength: (int)OrgBrandingRules.Logo.MaxBytes + 4096);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.SetImageAsync(_org.Id, BrandingImageKind.Logo, new MemoryStream(bytes)));

        Assert.Equal(OrgBrandingRules.ImageTooLargeCode, ex.Code);
    }

    [Fact]
    public async Task RemoveImageAsync_UploadedHero_ClearsUrlAndDeletesObject()
    {
        var url = (await _service.SetImageAsync(_org.Id, BrandingImageKind.Hero, new MemoryStream(TestImageBytes.WebPExtended(2400, 900))))!.HeroImageUrl!;
        var key = _storage.TryGetPublicKey(url)!;

        var org = await _service.RemoveImageAsync(_org.Id, BrandingImageKind.Hero);

        Assert.Null(org!.HeroImageUrl);
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, key));
    }

    [Fact]
    public async Task RemoveImageAsync_UrlOfAnotherObject_ClearsUrlButKeepsObject()
    {
        // A hero pointing at a property photo (set before BK-12): removing the hero must not delete the photo.
        var photoKey = StorageKeys.PropertyPhoto(Guid.NewGuid(), "photo.jpg");
        await _storage.PutAsync(StorageBucket.Public, photoKey, new MemoryStream([1, 2, 3]), "image/jpeg");
        var tracked = await _db.Orgs.FirstAsync(o => o.Id == _org.Id);
        tracked.HeroImageUrl = _storage.GetPublicUrl(photoKey);
        await _db.SaveChangesAsync();

        var org = await _service.RemoveImageAsync(_org.Id, BrandingImageKind.Hero);

        Assert.Null(org!.HeroImageUrl);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, photoKey));
    }

    [Fact]
    public async Task SetImageAsync_UnknownOrg_ReturnsNullAndStoresNothing()
    {
        var org = await _service.SetImageAsync(Guid.NewGuid(), BrandingImageKind.Logo, new MemoryStream(TestImageBytes.Png(400, 120)));

        Assert.Null(org);
        Assert.False(Directory.Exists(Path.Combine(_root, "public", "orgs")));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
