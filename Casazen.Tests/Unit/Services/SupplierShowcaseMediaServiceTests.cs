using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.ShowcaseTestImages;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="SupplierShowcaseMediaService"/> (SP-13b) on the filesystem storage and an in-memory database: what is accepted is decided on the
/// content (an SVG, or a text named <c>.jpg</c>, is a 415 whatever its name says), the GPS position and the other metadata are not in the stored
/// file, the limits answer 413 and 422 without storing anything, and the nightly cleanup removes the files nobody uses and never one that is
/// in use. The lock and the parallel uploads are a matter of the PostgreSQL run of the CI.
/// </summary>
public class SupplierShowcaseMediaServiceTests : IDisposable
{
    private const string PublicBaseUrl = "https://storage.test/public";

    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "casazen-test-showcase-media", Guid.NewGuid().ToString("N"));
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero));
    private readonly FileSystemFileStorage _storage;

    /// <summary>The keys the (mocked) content points to: the draft, the published version or the one before it.</summary>
    private readonly HashSet<string> _inUse = new(StringComparer.Ordinal);

    public SupplierShowcaseMediaServiceTests()
    {
        var options = Options.Create(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            PublicBaseUrl = PublicBaseUrl,
            FileSystem = new FileSystemStorageOptions { RootPath = _root },
        });
        _storage = new FileSystemFileStorage(options, NullLogger<FileSystemFileStorage>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    // ─── What is accepted ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_APhoneJpegWithAGpsPosition_IsStoredWithoutIt_AndKeepsItsOrientation()
    {
        var item = await Service().UploadAsync(OrgA, "cover", Form("IMG_0042.JPG", "image/jpeg", PhoneJpeg(orientation: 6)));

        Assert.Equal("cover", item.Kind);
        Assert.StartsWith($"{PublicBaseUrl}/suppliers/{OrgA}/showcase/cover-", item.Url);
        Assert.EndsWith(".jpg", item.Url);
        Assert.Equal("image/jpeg", item.ContentType);
        Assert.Equal(16, item.Width);
        Assert.Equal(32, item.Height);

        var stored = await ReadStoredAsync(item.Url);
        Assert.Equal(CleanedPhoneJpeg(6), stored);
        var text = Encoding.Latin1.GetString(stored);
        Assert.DoesNotContain("GPS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Via Roma", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IMG_0042", text, StringComparison.Ordinal);

        await using var db = CreateDb();
        var row = await db.SupplierShowcaseMedia.SingleAsync();
        Assert.Equal(OrgA, row.OrgId);
        Assert.Equal("cover", row.Kind);
        Assert.Equal("image/jpeg", row.ContentType);
        Assert.Equal(stored.Length, row.Bytes);
        Assert.Equal(_storage.TryGetPublicKey(item.Url), row.StorageKey);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, row.CreatedAt);
    }

    [Fact]
    public async Task Upload_APngAndAWebP_AreStoredWithoutTheirMetadata()
    {
        var png = await Service().UploadAsync(OrgA, "work", Form("lavoro.png", "image/png", PhonePng()));
        var webp = await Service().UploadAsync(OrgA, "before", Form("prima.webp", "image/webp", PhoneWebP()));

        Assert.EndsWith(".png", png.Url);
        Assert.EndsWith(".webp", webp.Url);
        Assert.Equal(CleanedPhonePng(), await ReadStoredAsync(png.Url));
        Assert.Equal(CleanedPhoneWebP(), await ReadStoredAsync(webp.Url));
        Assert.Equal(32, png.Width);
        Assert.Equal(16, png.Height);
    }

    [Fact]
    public async Task Upload_TheTypeIsTheContents_NotTheNameOrTheDeclaredType()
    {
        // A JPEG that calls itself a PNG with the wrong extension: it is a JPEG, stored as one.
        var item = await Service().UploadAsync(OrgA, "work", Form("foto.png", "image/png", PhoneJpeg(orientation: 1)));

        Assert.EndsWith(".jpg", item.Url);
        Assert.Equal("image/jpeg", item.ContentType);
    }

    // ─── What is refused, and nothing is stored ──────────────────────────────────────────────────

    [Theory]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("logo.png", "image/png")]
    [InlineData("logo", "")]
    public async Task Upload_AnSvg_IsA415WhateverItsNameSays_AndNothingIsStored(string name, string declaredType)
    {
        var svg = Ascii("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(document.cookie)</script></svg>");

        var refused = await Assert.ThrowsAsync<DomainUnsupportedMediaTypeException>(
            () => Service().UploadAsync(OrgA, "logo", Form(name, declaredType, svg)));

        Assert.Equal(ShowcaseMediaErrors.UnsupportedType, refused.Code);
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData("Questo non e un'immagine, e un testo rinominato.")]
    [InlineData("%PDF-1.7 not a picture")]
    [InlineData("GIF89a....")]
    public async Task Upload_AFakeImage_IsA415_AndNothingIsStored(string content)
    {
        var refused = await Assert.ThrowsAsync<DomainUnsupportedMediaTypeException>(
            () => Service().UploadAsync(OrgA, "work", Form("foto.jpg", "image/jpeg", Ascii(content))));

        Assert.Equal(ShowcaseMediaErrors.UnsupportedType, refused.Code);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Upload_AFileThatOnlyStartsLikeAnImage_IsA422_AndNothingIsStored()
    {
        // The first bytes of a JPEG and nothing else: no frame, no scan.
        byte[] stub = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().UploadAsync(OrgA, "work", Form("foto.jpg", "image/jpeg", stub)));

        Assert.Equal(ShowcaseMediaErrors.InvalidImage, refused.Code);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Upload_ALogoOver200Kb_AndAPhotoOver10Mb_AreA413_AndNothingIsStored()
    {
        var logo = new byte[(int)ShowcaseMediaLimits.LogoMaxBytes + 1];
        var photo = new byte[(int)ShowcaseMediaLimits.PhotoMaxBytes + 1];

        var logoRefused = await Assert.ThrowsAsync<DomainPayloadTooLargeException>(() => Service().UploadAsync(OrgA, "logo", Form("logo.png", "image/png", logo)));
        var photoRefused = await Assert.ThrowsAsync<DomainPayloadTooLargeException>(() => Service().UploadAsync(OrgA, "cover", Form("foto.jpg", "image/jpeg", photo)));

        Assert.Equal(ShowcaseMediaErrors.TooLarge, logoRefused.Code);
        Assert.Equal("ShowcaseMediaLogoTooLarge", logoRefused.MessageKey);
        Assert.Equal(ShowcaseMediaErrors.TooLarge, photoRefused.Code);
        Assert.Equal("ShowcaseMediaPhotoTooLarge", photoRefused.MessageKey);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Upload_TheLimitDependsOnTheKind_AFileOver200KbIsATooLargeLogoButAnAcceptablePhoto()
    {
        // 200 KB + 1 is a logo too large and a photo like any other: the limit depends on the kind.
        var file = Concat(PhonePng(), new byte[(int)ShowcaseMediaLimits.LogoMaxBytes]);

        await Assert.ThrowsAsync<DomainPayloadTooLargeException>(() => Service().UploadAsync(OrgA, "logo", Form("logo.png", "image/png", file)));
        var stored = await Service().UploadAsync(OrgA, "work", Form("lavoro.png", "image/png", file));

        Assert.Equal("work", stored.Kind);
    }

    [Fact]
    public async Task Upload_APictureWithTooManyPixels_IsA422_AndNothingIsStored()
    {
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().UploadAsync(OrgA, "cover", Form("enorme.png", "image/png", PhonePng(width: 20_000, height: 20_000))));

        Assert.Equal(ShowcaseMediaErrors.Dimensions, refused.Code);
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("avatar")]
    [InlineData("../logo")]
    public async Task Upload_WithNoKindOrAnUnknownOne_IsA422(string? kind)
    {
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().UploadAsync(OrgA, kind, Form("foto.jpg", "image/jpeg", PhoneJpeg())));

        Assert.Equal(ShowcaseMediaErrors.KindInvalid, refused.Code);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Upload_WithNoFile_IsA422()
    {
        var none = await Assert.ThrowsAsync<DomainRuleException>(() => Service().UploadAsync(OrgA, "cover", null));
        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => Service().UploadAsync(OrgA, "cover", Form("foto.jpg", "image/jpeg", [])));

        Assert.Equal(ShowcaseMediaErrors.None, none.Code);
        Assert.Equal(ShowcaseMediaErrors.None, empty.Code);
    }

    [Fact]
    public async Task Upload_BeyondTheNumberOfFilesASupplierMayStore_IsA422_AndTheOtherSupplierIsNotAffected()
    {
        await using (var db = CreateDb())
        {
            for (var i = 0; i < ShowcaseMediaLimits.MaxStoredFiles; i++)
                db.SupplierShowcaseMedia.Add(Row(OrgA, $"suppliers/{OrgA}/showcase/work-{i:D32}.jpg", _clock.GetUtcNow().UtcDateTime));

            await db.SaveChangesAsync();
        }

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service().UploadAsync(OrgA, "work", Form("foto.jpg", "image/jpeg", PhoneJpeg())));
        var other = await Service().UploadAsync(OrgB, "work", Form("foto.jpg", "image/jpeg", PhoneJpeg()));

        Assert.Equal(ShowcaseMediaErrors.LimitReached, refused.Code);
        Assert.StartsWith($"{PublicBaseUrl}/suppliers/{OrgB}/showcase/", other.Url);
        Assert.Single(StoredFiles());
    }

    // ─── Removing one ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AFileThatTheContentStillPointsTo_IsA409_AndTheFileStays()
    {
        var item = await Service().UploadAsync(OrgA, "cover", Form("foto.jpg", "image/jpeg", PhoneJpeg()));
        _inUse.Add(KeyOf(item));

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => Service().DeleteAsync(OrgA, item.Id));

        Assert.Equal(ShowcaseMediaErrors.InUse, refused.Code);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(item)));
        await using var db = CreateDb();
        Assert.Single(await db.SupplierShowcaseMedia.ToListAsync());
    }

    [Fact]
    public async Task Delete_AFileNobodyUses_RemovesTheObjectAndTheRow()
    {
        var item = await Service().UploadAsync(OrgA, "cover", Form("foto.jpg", "image/jpeg", PhoneJpeg()));

        await Service().DeleteAsync(OrgA, item.Id);

        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Delete_TheFileOfAnotherSupplier_IsA404_AndTheFileStays()
    {
        var item = await Service().UploadAsync(OrgA, "cover", Form("foto.jpg", "image/jpeg", PhoneJpeg()));

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteAsync(OrgB, item.Id));

        Assert.Equal(ShowcaseMediaErrors.NotFound, missing.Code);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(item)));
    }

    // ─── The nightly cleanup ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cleanup_RemovesTheOldFilesNobodyUses_AndNeverAFileInUse_OrAYoungOne()
    {
        var inUse = await Service().UploadAsync(OrgA, "cover", Form("a.jpg", "image/jpeg", PhoneJpeg()));
        var unused = await Service().UploadAsync(OrgA, "work", Form("b.jpg", "image/jpeg", PhoneJpeg()));
        var otherSupplier = await Service().UploadAsync(OrgB, "work", Form("c.png", "image/png", PhonePng()));

        // Two days later: the first three are old, a fourth is young and nobody uses it yet.
        _clock.Advance(TimeSpan.FromDays(2));
        var young = await Service().UploadAsync(OrgA, "work", Form("d.jpg", "image/jpeg", PhoneJpeg()));
        _inUse.Add(KeyOf(inUse));

        var result = await Service().CleanupAsync();

        Assert.False(result.Skipped);
        Assert.Equal(2, result.Removed);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(inUse)));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(unused)));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(otherSupplier)));
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(young)));

        await using var db = CreateDb();
        var left = await db.SupplierShowcaseMedia.Select(m => m.StorageKey).ToListAsync();
        Assert.Equal(new[] { KeyOf(inUse), KeyOf(young) }.Order(), left.Order());
    }

    [Fact]
    public async Task Cleanup_ARunAfterTheFirstFindsNothingMore_AndAFileThatBecomesUnusedGoesAtTheNextRun()
    {
        var item = await Service().UploadAsync(OrgA, "cover", Form("a.jpg", "image/jpeg", PhoneJpeg()));
        _inUse.Add(KeyOf(item));
        _clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(0, (await Service().CleanupAsync()).Removed);
        Assert.Equal(0, (await Service().CleanupAsync()).Removed);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(item)));

        // The supplier takes it out of the content (and out of the version before it): the next night it goes.
        _inUse.Clear();
        Assert.Equal(1, (await Service().CleanupAsync()).Removed);
        Assert.Equal(0, (await Service().CleanupAsync()).Removed);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Cleanup_AFileJustUnderTheGraceIsKept_EvenWhenNobodyUsesIt()
    {
        var item = await Service().UploadAsync(OrgA, "work", Form("a.jpg", "image/jpeg", PhoneJpeg()));
        _clock.Advance(ShowcaseMediaLimits.OrphanGrace - TimeSpan.FromMinutes(1));

        Assert.Equal(0, (await Service().CleanupAsync()).Removed);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, KeyOf(item)));

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, (await Service().CleanupAsync()).Removed);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IFormFile Form(string fileName, string contentType, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>A service on its own context, as every request gets its own.</summary>
    private SupplierShowcaseMediaService Service()
    {
        var content = new Mock<ISupplierShowcaseContentService>();
        content
            .Setup(c => c.FindKeysInUseAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<string> keys, CancellationToken _) =>
                (IReadOnlySet<string>)keys.Where(_inUse.Contains).ToHashSet(StringComparer.Ordinal));

        return new SupplierShowcaseMediaService(
            CreateDb(), _storage, content.Object, NullLogger<SupplierShowcaseMediaService>.Instance, _clock);
    }

    private static SupplierShowcaseMedia Row(Guid orgId, string key, DateTime createdAt) => new()
    {
        OrgId = orgId,
        Kind = "work",
        StorageKey = key,
        ContentType = "image/jpeg",
        Bytes = 100,
        CreatedAt = createdAt,
    };

    private string KeyOf(ShowcaseMediaItem item) => _storage.TryGetPublicKey(item.Url)!;

    private async Task<byte[]> ReadStoredAsync(string url)
    {
        var key = _storage.TryGetPublicKey(url);
        Assert.NotNull(key);
        await using var stream = await _storage.OpenReadAsync(StorageBucket.Public, key);
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private string[] StoredFiles() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];

    private async Task AssertNothingStoredAsync()
    {
        Assert.Empty(StoredFiles());
        await using var db = CreateDb();
        Assert.Empty(await db.SupplierShowcaseMedia.ToListAsync());
    }
}
