using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: the photos of the work. The supplier adds them while the work is on; they are kept in the <b>private</b> bucket under
/// the request, listed on the request, and read back only through <c>OpenWorkPhotoAsync</c> (the controller authorizes the
/// request first). Every file is checked on its content before any is stored, and a failed save leaves no object behind.
/// </summary>
public class ServiceRequestPhotosTests
{
    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task AddWorkPhotosAsync_TakenRequest_StoresTheFilesInThePrivateBucketUnderTheRequestAndListsThem()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        var updated = await s.Service.AddWorkPhotosAsync(
            taken.Id, s.SupplierOrgId, [Image("prima.jpg", "image/jpeg", JpegBytes), Image("dopo.PNG", "image/png", PngBytes)]);

        var photos = ServiceRequestJson.ReadPhotos((await s.ReadAsync(taken.Id)).WorkPhotosJson);
        Assert.Equal(2, photos.Count);
        Assert.Equal(photos, ServiceRequestJson.ReadPhotos(updated.WorkPhotosJson));
        Assert.All(photos, photo =>
        {
            Assert.StartsWith($"service-requests/{taken.Id}/photos/", photo.Key);
            Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, photo.UploadedAt);
        });
        Assert.NotEqual(photos[0].Id, photos[1].Id);
        Assert.Equal(2, StoredObjects(s, StorageBucket.Private).Length);
        Assert.Empty(StoredObjects(s, StorageBucket.Public));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_RequestInCorso_Works()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var started = await s.StartedAsync();

        await s.Service.AddWorkPhotosAsync(started.Id, s.SupplierOrgId, [Image("lavoro.jpg", "image/jpeg", JpegBytes)]);

        Assert.Single(ServiceRequestJson.ReadPhotos((await s.ReadAsync(started.Id)).WorkPhotosJson));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_AnotherUploadLater_AddsToTheList()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("uno.jpg", "image/jpeg", JpegBytes)]);

        await s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("due.png", "image/png", PngBytes)]);

        Assert.Equal(2, ServiceRequestJson.ReadPhotos((await s.ReadAsync(taken.Id)).WorkPhotosJson).Count);
        Assert.Equal(2, StoredObjects(s, StorageBucket.Private).Length);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task AddWorkPhotosAsync_RequestNotInProgress_Throws422AndStoresNothing(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AddWorkPhotosAsync(request.Id, s.SupplierOrgId, [Image("lavoro.jpg", "image/jpeg", JpegBytes)]));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotAddPhotosMessageKey, ex.MessageKey);
        Assert.Empty(StoredObjects(s, StorageBucket.Private));
        Assert.Equal("[]", (await s.ReadAsync(request.Id)).WorkPhotosJson);
    }

    [Fact]
    public async Task AddWorkPhotosAsync_UpToTheLimit_IsAcceptedAndOneMoreIsNot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var files = Enumerable.Range(0, ServiceRequestLimits.MaxWorkPhotos).Select(i => Image($"foto-{i}.jpg", "image/jpeg", JpegBytes)).ToList();
        await s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, files);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("una-di-troppo.jpg", "image/jpeg", JpegBytes)]));

        Assert.Equal(ServiceRequestErrorCodes.PhotoLimitReached, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.PhotoLimitReachedMessageKey, ex.MessageKey);
        Assert.Equal(new object[] { ServiceRequestLimits.MaxWorkPhotos, ServiceRequestLimits.MaxWorkPhotos, 1 }, ex.MessageArgs);
        Assert.Equal(ServiceRequestLimits.MaxWorkPhotos, StoredObjects(s, StorageBucket.Private).Length);
    }

    [Fact]
    public async Task AddWorkPhotosAsync_MoreFilesThanTheLimitAtOnce_ThrowsBeforeStoringAnything()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var files = Enumerable.Range(0, ServiceRequestLimits.MaxWorkPhotos + 1).Select(i => Image($"foto-{i}.jpg", "image/jpeg", JpegBytes)).ToList();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, files));

        Assert.Equal(ServiceRequestErrorCodes.PhotoLimitReached, ex.Code);
        Assert.Empty(StoredObjects(s, StorageBucket.Private));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_ATextRenamedToJpg_IsRefusedOnItsContentAndNothingIsStored()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var disguised = "ciao, non sono una foto"u8.ToArray();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AddWorkPhotosAsync(
            taken.Id, s.SupplierOrgId, [Image("ok.jpg", "image/jpeg", JpegBytes), Image("finta.jpg", "image/jpeg", disguised)]));

        Assert.Equal(ServiceRequestErrorCodes.PhotoInvalid, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.PhotoInvalidMessageKey, ex.MessageKey);
        Assert.Equal("finta.jpg", Assert.Single(ex.MessageArgs));
        Assert.Empty(StoredObjects(s, StorageBucket.Private));
        Assert.Equal("[]", (await s.ReadAsync(taken.Id)).WorkPhotosJson);
    }

    [Theory]
    [InlineData("foto.gif", "image/gif")]
    [InlineData("foto.pdf", "application/pdf")]
    [InlineData("foto.jpg", "image/png")] // the declared type and the extension must agree with the content
    [InlineData("foto.png", "image/jpeg")]
    public async Task AddWorkPhotosAsync_UnsupportedOrInconsistentType_Throws422PhotoInvalid(string fileName, string contentType)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image(fileName, contentType, JpegBytes)]));

        Assert.Equal(ServiceRequestErrorCodes.PhotoInvalid, ex.Code);
        Assert.Empty(StoredObjects(s, StorageBucket.Private));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_EmptyOrOversizedFile_Throws422PhotoInvalid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("vuota.jpg", "image/jpeg", [])]));
        var huge = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AddWorkPhotosAsync(
            taken.Id, s.SupplierOrgId, [FakeSized("grande.jpg", "image/jpeg", ServiceRequestLimits.MaxPhotoFileSizeBytes + 1)]));

        Assert.Equal(ServiceRequestErrorCodes.PhotoInvalid, empty.Code);
        Assert.Equal(ServiceRequestErrorCodes.PhotoInvalid, huge.Code);
        Assert.Equal("grande.jpg", Assert.Single(huge.MessageArgs));
        Assert.Empty(StoredObjects(s, StorageBucket.Private));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_NoFile_IsAProgrammingErrorTheControllerNeverSends()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, []));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_RequestOfAnotherSupplier_IsForbiddenAndStoresNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var other = await s.AddOtherSupplierAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Service.AddWorkPhotosAsync(taken.Id, other, [Image("lavoro.jpg", "image/jpeg", JpegBytes)]));

        Assert.Empty(StoredObjects(s, StorageBucket.Private));
    }

    [Fact]
    public async Task AddWorkPhotosAsync_UnknownRequest_Throws404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => s.Service.AddWorkPhotosAsync(Guid.NewGuid(), s.SupplierOrgId, [Image("lavoro.jpg", "image/jpeg", JpegBytes)]));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task AddWorkPhotosAsync_SuspendedSupplier_Throws422SupplierNotActive()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("lavoro.jpg", "image/jpeg", JpegBytes)]));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, ex.Code);
    }

    [Fact]
    public async Task AddWorkPhotosAsync_RequestChangedUnderTheUpload_Throws409AndRemovesTheObjectsItStored()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var taken = await s.TakenAsync();
        interceptor.Failure = new DbUpdateConcurrencyException("the request changed");

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.AddWorkPhotosAsync(
            taken.Id, s.SupplierOrgId, [Image("uno.jpg", "image/jpeg", JpegBytes), Image("due.png", "image/png", PngBytes)]));

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
        Assert.Equal(1, interceptor.Refused);
        // Nothing is left that no request lists.
        Assert.Empty(StoredObjects(s, StorageBucket.Private));
    }

    // ─── Reading a photo back ───

    [Fact]
    public async Task OpenWorkPhotoAsync_ListedPhoto_ReturnsItsBytesItsTypeAndANeutralName()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.AddWorkPhotosAsync(
            taken.Id, s.SupplierOrgId, [Image("Foto Cliente Mario Rossi.jpg", "image/jpeg", JpegBytes), Image("dopo.png", "image/png", PngBytes)]);
        var saved = await s.ReadAsync(taken.Id);
        var photos = ServiceRequestJson.ReadPhotos(saved.WorkPhotosJson);

        var first = await s.Service.OpenWorkPhotoAsync(saved, photos[0].Id);
        var second = await s.Service.OpenWorkPhotoAsync(saved, photos[1].Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        await using (first.Content)
        await using (second.Content)
        {
            Assert.Equal("image/jpeg", first.ContentType);
            Assert.Equal("lavoro-1.jpg", first.FileName);
            Assert.Equal(JpegBytes, await ReadAllAsync(first.Content));
            Assert.Equal("image/png", second.ContentType);
            Assert.Equal("lavoro-2.png", second.FileName);
            Assert.Equal(PngBytes, await ReadAllAsync(second.Content));
        }
    }

    [Fact]
    public async Task OpenWorkPhotoAsync_PhotoNotOnTheRequest_ReturnsNull()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("uno.jpg", "image/jpeg", JpegBytes)]);
        var saved = await s.ReadAsync(taken.Id);

        Assert.Null(await s.Service.OpenWorkPhotoAsync(saved, Guid.NewGuid()));
    }

    [Fact]
    public async Task OpenWorkPhotoAsync_AKeyOutsideTheFolderOfTheRequest_IsNeverRead()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var another = await s.TakenAsync();
        await s.Service.AddWorkPhotosAsync(another.Id, s.SupplierOrgId, [Image("altro.jpg", "image/jpeg", JpegBytes)]);
        var anotherPhoto = ServiceRequestJson.ReadPhotos((await s.ReadAsync(another.Id)).WorkPhotosJson).Single();

        // A list that points at the file of another request (a tampered column) answers like a missing photo.
        var tampered = await s.ReadAsync(taken.Id);
        tampered.WorkPhotosJson = ServiceRequestJson.Serialize([new ServiceRequestPhoto(Guid.NewGuid(), anotherPhoto.Key, DateTime.UtcNow)]);
        var listed = ServiceRequestJson.ReadPhotos(tampered.WorkPhotosJson).Single();

        Assert.Null(await s.Service.OpenWorkPhotoAsync(tampered, listed.Id));
    }

    [Fact]
    public async Task OpenWorkPhotoAsync_FileMissingFromTheStorage_ReturnsNull()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.AddWorkPhotosAsync(taken.Id, s.SupplierOrgId, [Image("uno.jpg", "image/jpeg", JpegBytes)]);
        foreach (var file in StoredObjects(s, StorageBucket.Private))
            File.Delete(file);
        var saved = await s.ReadAsync(taken.Id);

        Assert.Null(await s.Service.OpenWorkPhotoAsync(saved, ServiceRequestJson.ReadPhotos(saved.WorkPhotosJson).Single().Id));
    }

    // ─── helpers ───

    private static string[] StoredObjects(ServiceRequestScenario s, StorageBucket bucket)
    {
        var root = s.Kit.Storage.BucketRoot(bucket);
        return Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static IFormFile Image(string fileName, string contentType, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "photos", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    /// <summary>A file that claims to be <paramref name="length"/> bytes long without allocating them.</summary>
    private static IFormFile FakeSized(string fileName, string contentType, long length) =>
        new FormFile(new MemoryStream(JpegBytes), 0, length, "photos", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
}
