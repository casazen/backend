using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US02/T01 (NF-195, ADR-013-R20, decision D9): the evidence kept for the reviewer of a self-hosted verification. Only with the three flags on, never for a property that
/// keeps no document; the crop of the portrait (a PNG, scaled) and at most <c>Evidence:MaxFrames</c> of the frames the server chose (the JPEG received without its metadata, or a
/// scaled PNG), each encrypted with its own sub-purpose, with a row that carries the SHA-256 of the ciphertext and its expiry; one set per booking; an error keeps nothing. The
/// pictures are generated in code: no photograph of anybody is in the repository.
/// </summary>
public sealed class IdentityReviewEvidenceStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly DetectedFace Portrait = new(60, 20, 40, 50, 0.99f, new float[10], 0.08);

    [Fact]
    public async Task IdentityReviewEvidenceStore_StoresOnlyCiphertextOfCropAndChosenFrames()
    {
        await using var world = new EvidenceWorld();
        var (document, documentPixels) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: true);
        var input = world.Input(document, sequence, chosen: [0, 2, 3, 1]);

        var kept = await world.Store().SaveForReviewAsync(input, Ct);

        Assert.Equal(4, kept);
        var rows = await world.RowsAsync();
        Assert.Equal(4, rows.Count);
        Assert.Single(rows, row => row.Kind == IdentityEvidenceKind.DocumentPortrait);
        Assert.Equal(3, rows.Count(row => row.Kind == IdentityEvidenceKind.Frame));
        Assert.Equal(4, world.Storage.Objects.Count);
        foreach (var row in rows)
        {
            Assert.Equal(IdentityReviewEvidenceStore.StorageKeyOf(world.OrgId, world.BookingId, row.Id), row.StorageKey);
            Assert.StartsWith($"identity-evidence/{world.OrgId}/{world.BookingId}/", row.StorageKey, StringComparison.Ordinal);
            Assert.Equal((world.SessionId, world.GuestId, world.LogId), (row.CheckInSessionId, row.GuestId, row.VerificationLogId));
            Assert.Equal(Now.UtcDateTime.AddHours(48), row.ExpiresAt);
            Assert.Null(row.DeletedAt);

            var stored = world.Storage.Objects[row.StorageKey];
            Assert.False(StartsWith(stored, PngSignature), "a PNG reached the storage in clear");
            Assert.False(StartsWith(stored, [0xFF, 0xD8]), "a JPEG reached the storage in clear");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stored)), row.Sha256);
        }

        var cropRow = rows.Single(row => row.Kind == IdentityEvidenceKind.DocumentPortrait);
        Assert.Equal(IdentityReviewEvidenceStore.PngContentType, cropRow.ContentType);
        var crop = world.Decrypt(cropRow);
        Assert.True(StartsWith(crop, PngSignature));
        using (var decoded = RasterImage.Decode(crop, 1_000_000))
        using (var expected = documentPixels.Cropped(50, 7, 60, 76))
        {
            Assert.Equal((60, 76), (decoded.Width, decoded.Height));
            Assert.Equal(expected.Rgb, decoded.Rgb);
        }

        var frameRows = rows.Where(row => row.Kind == IdentityEvidenceKind.Frame).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToList();
        var frames = frameRows.Select(row => (row, bytes: world.Decrypt(row))).ToList();
        Assert.All(frames, frame =>
        {
            Assert.Equal(IdentityReviewEvidenceStore.JpegContentType, frame.row.ContentType);
            var markers = JpegMetadata.SegmentMarkers(frame.bytes);
            Assert.DoesNotContain(markers, marker => marker is >= 0xE0 and <= 0xEF or 0xFE);
            Assert.Equal(0xD9, markers[^1]);
            Assert.False(Contains(frame.bytes, Encoding.ASCII.GetBytes("TEST-PHONE")), "the comment of the device is still in the frame");
            Assert.False(Contains(frame.bytes, Encoding.ASCII.GetBytes("TRAILER")), "what followed the end of the image is still in the frame");
            using var picture = RasterImage.Decode(frame.bytes, 1_000_000);
            Assert.Equal((32, 24), (picture.Width, picture.Height));
        });
        var chosen = new[] { 0, 2, 3 }.Select(index => Strip(sequence.Frames[index].Jpeg)).ToList();
        Assert.All(frames, frame => Assert.Contains(chosen, expected => expected.SequenceEqual(frame.bytes)));
        Assert.Equal(3, frames.Select(frame => Convert.ToHexString(frame.bytes)).Distinct().Count());

        Assert.DoesNotContain(typeof(IdentityReviewEvidence).GetProperties(), property => property.PropertyType == typeof(byte[]) || property.PropertyType == typeof(float[]));
        Assert.DoesNotContain(
            typeof(IdentityReviewEvidenceStore).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(IFaceEmbedder));
    }

    [Fact]
    public async Task IdentityReviewEvidenceStore_TicinoProperty_StoresNothing()
    {
        await using var world = new EvidenceWorld();
        var (document, _) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: false);

        var kept = await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1, 2], keepsNoDocument: true), Ct);

        Assert.Equal(0, kept);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.RowsAsync());
        Assert.Empty(world.Detector.Calls);
    }

    [Theory]
    [InlineData(FeatureFlags.CheckInIdentityReviewEvidence)]
    [InlineData(FeatureFlags.CheckInIdentitySelfHosted)]
    [InlineData(FeatureFlags.CheckInIdentityVerification)]
    public async Task IdentityReviewEvidenceStore_AnyFlagOff_StoresNothing(string off)
    {
        await using var world = new EvidenceWorld();
        world.Flags.Set(off, false);
        var (document, _) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: false);

        var kept = await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1, 2]), Ct);

        Assert.Equal(0, kept);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.RowsAsync());
    }

    [Fact]
    public async Task IdentityReviewEvidenceStore_LargePictures_AreScaledToMaxSide_AndATurnedFrameIsKeptUprightAsPng()
    {
        await using var world = new EvidenceWorld();
        world.Limits.Evidence.MaxSide = 64;
        world.Limits.Evidence.MaxFrames = 2;
        var (document, _) = Document(400, 240, seed: 9);
        world.Detector.Fallback = _ => [new DetectedFace(100, 40, 120, 150, 0.99f, new float[10], 0.2)];

        var large = TestJpeg.Noise(96, 72, seed: 3);
        var turned = WithSegments(TestJpeg.Noise(40, 30, seed: 4), App1(ExifOrientationSix()));
        using var sequence = new FrameSequence(
            [
                new SequenceFrame(0, 0, RasterImage.Decode(large, 1_000_000), large),
                new SequenceFrame(1, 125, RasterImage.Decode(turned, 1_000_000), turned),
            ],
            Now);

        var kept = await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1]), Ct);

        Assert.Equal(3, kept);
        foreach (var row in await world.RowsAsync())
        {
            Assert.Equal(IdentityReviewEvidenceStore.PngContentType, row.ContentType);
            using var picture = RasterImage.Decode(world.Decrypt(row), 1_000_000);
            Assert.True(Math.Max(picture.Width, picture.Height) <= 64, $"{row.Kind} is {picture.Width}x{picture.Height}");
            if (row.Kind == IdentityEvidenceKind.Frame && picture.Width < picture.Height)
                Assert.Equal((30, 40), (picture.Width, picture.Height));
        }
    }

    [Fact]
    public async Task IdentityReviewEvidenceStore_MaxFramesZero_KeepsTheCropOnly_AndNoPortraitKeepsTheFramesOnly()
    {
        await using var world = new EvidenceWorld();
        world.Limits.Evidence.MaxFrames = 0;
        var (document, _) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: false);

        Assert.Equal(1, await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1, 2]), Ct));
        Assert.Equal(IdentityEvidenceKind.DocumentPortrait, Assert.Single(await world.RowsAsync(), row => row.DeletedAt == null).Kind);

        world.Limits.Evidence.MaxFrames = 3;
        world.Detector.Fallback = null;
        Assert.Equal(3, await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1, 2]), Ct));
        Assert.All(await world.RowsAsync(row => row.DeletedAt == null), row => Assert.Equal(IdentityEvidenceKind.Frame, row.Kind));
    }

    [Fact]
    public async Task IdentityReviewEvidenceStore_OneSetPerBooking_TheEarlierSetIsDeleted()
    {
        await using var world = new EvidenceWorld();
        var (document, _) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: false);
        await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1]), Ct);
        var first = await world.RowsAsync();

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [2, 3]), Ct);

        var rows = await world.RowsAsync();
        Assert.All(rows.Where(row => first.Any(old => old.Id == row.Id)), row =>
        {
            Assert.Equal(IdentityEvidenceDeletionReasons.Replaced, row.DeletionReason);
            Assert.Equal(Now.UtcDateTime.AddMinutes(5), row.DeletedAt);
            Assert.False(world.Storage.Objects.ContainsKey(row.StorageKey));
        });
        var live = rows.Where(row => row.DeletedAt == null).ToList();
        Assert.Equal(3, live.Count);
        Assert.Equal(live.Select(row => row.StorageKey).Order(), world.Storage.Objects.Keys.Order());
    }

    [Fact]
    public async Task IdentityReviewEvidenceStore_AStorageFailure_KeepsNoRowAndNoObject_AndLogsTheTypeOnly()
    {
        await using var world = new EvidenceWorld();
        world.Storage.FailPutAfter = 2;
        world.Storage.Failure = new InvalidOperationException("bucket down while writing the face of Anna Bianchi");
        var (document, _) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: false);
        var logger = new CapturingLogger<IdentityReviewEvidenceStore>();

        var kept = await world.Store(logger).SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1, 2]), Ct);

        Assert.Equal(0, kept);
        Assert.Empty(await world.RowsAsync());
        Assert.Empty(world.Storage.Objects);
        Assert.Contains(nameof(InvalidOperationException), logger.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", logger.AllOutput, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(world.Db.ChangeTracker.Entries<IdentityReviewEvidence>(), entry => entry.State == EntityState.Added);
    }

    [Fact]
    public async Task IdentityReviewEvidenceStore_ThePicturesInClear_AreZeroedOnceEncrypted()
    {
        await using var world = new EvidenceWorld();
        var (document, _) = Document(200, 120, seed: 7);
        using var sequence = Sequence(4, withMetadata: true);

        await world.Store().SaveForReviewAsync(world.Input(document, sequence, chosen: [0, 1, 2]), Ct);

        Assert.Equal(4, world.DataProtection.Protected.Count);
        Assert.All(world.DataProtection.Protected, plain => Assert.All(plain, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void JpegMetadata_TakesOutEveryApplicationSegmentAndComment_AndKeepsThePicture()
    {
        var plain = TestJpeg.Noise(24, 16, seed: 5);
        var dressed = WithSegments(plain, App1(Encoding.ASCII.GetBytes("Exif\0\0GPS 45.46N 9.19E")), Comment("TEST-PHONE"), App(0xE2, Encoding.ASCII.GetBytes("ICC_PROFILE")));
        dressed = [.. dressed, .. Encoding.ASCII.GetBytes("TRAILER")];

        Assert.True(JpegMetadata.TryStrip(dressed, out var stripped));

        Assert.DoesNotContain(JpegMetadata.SegmentMarkers(stripped), marker => marker is >= 0xE0 and <= 0xEF or 0xFE);
        Assert.Contains((byte)0xE1, JpegMetadata.SegmentMarkers(dressed));
        Assert.Equal(Strip(plain), stripped);
        using var before = RasterImage.Decode(plain, 1_000_000);
        using var after = RasterImage.Decode(stripped, 1_000_000);
        Assert.Equal(before.Rgb, after.Rgb);

        Assert.False(JpegMetadata.TryStrip(PngSignature, out _));
        Assert.False(JpegMetadata.TryStrip(dressed.AsSpan(0, dressed.Length / 2), out _));
        Assert.False(JpegMetadata.TryStrip([0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF, 0x00], out _));
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    internal static (byte[] Png, RasterImage Pixels) Document(int width, int height, int seed)
    {
        var png = TestImages.Png(width, height, seed);
        return (png, RasterImage.Decode(png, 1_000_000));
    }

    internal static FrameSequence Sequence(int count, bool withMetadata)
    {
        var frames = new List<SequenceFrame>(count);
        for (var index = 0; index < count; index++)
        {
            var jpeg = TestJpeg.Noise(32, 24, seed: 20 + index);
            if (withMetadata)
            {
                jpeg = WithSegments(jpeg, App(0xE0, Encoding.ASCII.GetBytes("JFIF\0")), App1(Encoding.ASCII.GetBytes("Exif\0\0no tiff here")), Comment("TEST-PHONE"));
                jpeg = [.. jpeg, .. Encoding.ASCII.GetBytes("TRAILER")];
            }

            frames.Add(new SequenceFrame(index, index * 125L, RasterImage.Decode(jpeg, 1_000_000), jpeg));
        }

        return new FrameSequence(frames, Now);
    }

    internal static byte[] Strip(byte[] jpeg)
    {
        Assert.True(JpegMetadata.TryStrip(jpeg, out var stripped));
        return stripped;
    }

    internal static byte[] WithSegments(byte[] jpeg, params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(segment => segment), .. jpeg.AsSpan(2).ToArray()];

    internal static byte[] App(byte marker, byte[] payload) => [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    internal static byte[] App1(byte[] payload) => App(0xE1, payload);

    internal static byte[] Comment(string text) => App(0xFE, Encoding.ASCII.GetBytes(text));

    private static byte[] ExifOrientationSix() =>
    [
        .. Encoding.ASCII.GetBytes("Exif\0\0"),
        (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
        1, 0,
        0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0,
        0, 0, 0, 0,
    ];

    private static bool StartsWith(byte[] bytes, byte[] prefix) => bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool Contains(byte[] bytes, byte[] part) => bytes.AsSpan().IndexOf(part) >= 0;

    internal sealed class EvidenceWorld : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"review-evidence-{Guid.NewGuid()}").Options;

        public EvidenceWorld()
        {
            Db = new AppDbContext(_options);
            Detector.Fallback = _ => [Portrait];
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public Guid BookingId { get; } = Guid.NewGuid();

        public Guid SessionId { get; } = Guid.NewGuid();

        public Guid GuestId { get; } = Guid.NewGuid();

        public Guid LogId { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public FailingMemoryStorage Storage { get; } = new();

        public RecordingDataProtection DataProtection { get; } = new(new EphemeralDataProtectionProvider());

        public FakeTimeProvider Clock { get; } = new(Now);

        public TestFeatureFlags Flags { get; } =
            new(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityReviewEvidence);

        public IdentityVerificationOptions Limits { get; } = new();

        public KeyedFaceDetector Detector { get; } = new();

        public IdentityReviewEvidenceStore Store(Microsoft.Extensions.Logging.ILogger<IdentityReviewEvidenceStore>? logger = null, IActivityLog? activityLog = null) =>
            new(
                Db,
                Storage,
                DataProtection,
                Flags,
                Options.Create(Limits),
                Options.Create(new BiometricModelOptions()),
                Detector,
                Clock,
                logger ?? new CapturingLogger<IdentityReviewEvidenceStore>(),
                activityLog);

        public ReviewEvidenceInput Input(byte[]? document, FrameSequence? sequence, IReadOnlyList<int>? chosen, bool keepsNoDocument = false) =>
            new(OrgId, BookingId, SessionId, GuestId, LogId, keepsNoDocument, document, sequence, chosen);

        public AppDbContext NewContext() => new(_options);

        public async Task<List<IdentityReviewEvidence>> RowsAsync(Func<IdentityReviewEvidence, bool>? where = null)
        {
            await using var read = NewContext();
            var rows = await read.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            return where is null ? rows : rows.Where(where).ToList();
        }

        public byte[] Decrypt(IdentityReviewEvidence row) =>
            DataProtection.Inner.CreateProtector(IdentityReviewEvidenceStore.ContentPurpose, row.Id.ToString("N")).Unprotect(Storage.Objects[row.StorageKey]);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

internal sealed class FailingMemoryStorage : IFileStorage
{
    private int _puts;

    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public List<string> Operations { get; } = [];

    public int? FailPutAfter { get; set; }

    public Exception? FailDeletes { get; set; }

    public Exception Failure { get; set; } = new IOException("The storage is down.");

    public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (FailPutAfter is { } after && _puts >= after)
            throw Failure;

        _puts++;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        lock (Objects)
        {
            Objects[key] = copy.ToArray();
            Operations.Add("put:" + key);
        }
    }

    public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        lock (Objects)
            return Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
    }

    public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        lock (Objects)
            return Task.FromResult(Objects.ContainsKey(key));
    }

    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        if (FailDeletes is { } failure)
            return Task.FromException(failure);

        lock (Objects)
        {
            Objects.Remove(key);
            Operations.Add("delete:" + key);
        }

        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key) => throw new NotSupportedException("The private bucket has no public URL.");

    public string? TryGetPublicKey(string url) => null;

    public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);
}

internal sealed class RecordingDataProtection(IDataProtectionProvider inner) : IDataProtectionProvider
{
    public IDataProtectionProvider Inner { get; } = inner;

    public List<byte[]> Protected { get; } = [];

    public IDataProtector CreateProtector(string purpose) => new Recording(Inner.CreateProtector(purpose), Protected);

    private sealed class Recording(IDataProtector protector, List<byte[]> protectedArrays) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new Recording(protector.CreateProtector(purpose), protectedArrays);

        public byte[] Protect(byte[] plaintext)
        {
            lock (protectedArrays)
                protectedArrays.Add(plaintext);

            return protector.Protect(plaintext);
        }

        public byte[] Unprotect(byte[] protectedData) => protector.Unprotect(protectedData);
    }
}
