using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="ImageStorageService"/> on the filesystem provider (FD-07): photos in the public bucket
/// with absolute URLs, documents in the private bucket referenced by key, never under wwwroot.
/// </summary>
public class ImageStorageServiceTests : IDisposable
{
    private const string PublicBaseUrl = "https://api.test/storage/public";

    private readonly string _root;
    private readonly FileSystemFileStorage _storage;
    private readonly ImageStorageService _service;

    public ImageStorageServiceTests()
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
        _service = new ImageStorageService(_storage, options, NullLogger<ImageStorageService>.Instance);
    }

    [Theory]
    [InlineData("test.jpg", "image/jpeg")]
    [InlineData("test.png", "image/png")]
    [InlineData("test.webp", "image/webp")]
    public void ValidateImage_WithAcceptedFormat_ReturnsTrue(string fileName, string contentType)
    {
        Assert.True(_service.ValidateImage(CreateFormFile(fileName, contentType, 1024)));
    }

    [Theory]
    [InlineData("test.txt", "text/plain", 1024)]
    [InlineData("test.jpg", "text/plain", 1024)]
    [InlineData("test.jpg", "image/jpeg", 11 * 1024 * 1024)]
    [InlineData("test.jpg", "image/jpeg", 0)]
    public void ValidateImage_WithInvalidFile_ReturnsFalse(string fileName, string contentType, long length)
    {
        Assert.False(_service.ValidateImage(CreateFormFile(fileName, contentType, length)));
    }

    [Fact]
    public void ValidateImage_WithNullFile_ReturnsFalse()
    {
        Assert.False(_service.ValidateImage(null!));
    }

    [Fact]
    public async Task UploadImageAsync_WithValidFile_StoresInPublicBucketAndReturnsAbsoluteUrl()
    {
        var propertyId = Guid.NewGuid();

        var url = await _service.UploadImageAsync(CreateFormFile("Foto.JPG", "image/jpeg", 1024), propertyId);

        Assert.StartsWith($"{PublicBaseUrl}/properties/{propertyId}/photos/", url);
        Assert.EndsWith(".jpg", url);
        Assert.True(Uri.IsWellFormedUriString(url, UriKind.Absolute));
        var key = _storage.TryGetPublicKey(url);
        Assert.NotNull(key);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Public, key!));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Private, key!));
    }

    [Fact]
    public async Task UploadSupplierPhotoAsync_WithValidFile_UsesSupplierFolder()
    {
        var orgId = Guid.NewGuid();

        var url = await _service.UploadSupplierPhotoAsync(CreateFormFile("a.png", "image/png", 512), orgId);

        Assert.StartsWith($"{PublicBaseUrl}/suppliers/{orgId}/photos/", url);
    }

    [Fact]
    public async Task UploadImageAsync_WithInvalidFile_ThrowsAndStoresNothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadImageAsync(CreateFormFile("test.txt", "text/plain", 1024), Guid.NewGuid()));

        Assert.False(Directory.Exists(Path.Combine(_root, "public")) && Directory.EnumerateFiles(Path.Combine(_root, "public"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task DeleteImageAsync_WithUploadedImage_DeletesObject()
    {
        var url = await _service.UploadImageAsync(CreateFormFile("test.jpg", "image/jpeg", 1024), Guid.NewGuid());

        await _service.DeleteImageAsync(url);

        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, _storage.TryGetPublicKey(url)!));
    }

    [Theory]
    [InlineData("/uploads/properties/nonexistent/file.jpg")]
    [InlineData("/uploads/properties/../../outside.jpg")]
    [InlineData("https://example.com/photo.jpg")]
    public async Task DeleteImageAsync_WithUrlNotIssuedByStorage_DoesNothing(string url)
    {
        var mock = new Mock<IFileStorage>();
        mock.Setup(s => s.TryGetPublicKey(url)).Returns((string?)null);
        var service = new ImageStorageService(mock.Object, Options.Create(new StorageOptions()), NullLogger<ImageStorageService>.Instance);

        await service.DeleteImageAsync(url);

        mock.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadDocumentAsync_WithValidFile_StoresInPrivateBucketAndReturnsKey()
    {
        var propertyId = Guid.NewGuid();

        var reference = await _service.UploadDocumentAsync(CreateFormFile("ape.pdf", "application/pdf", 2048), propertyId);

        Assert.StartsWith($"properties/{propertyId}/documents/", reference);
        Assert.DoesNotContain("://", reference);
        Assert.True(await _storage.ExistsAsync(StorageBucket.Private, reference));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, reference));
    }

    [Fact]
    public async Task OpenReadAsync_WithUploadedDocument_ReturnsStoredBytes()
    {
        var reference = await _service.UploadDocumentAsync(CreateFormFile("ape.pdf", "application/pdf", 2048), Guid.NewGuid());

        await using var stream = await _service.OpenReadAsync(reference);

        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        Assert.Equal(2048, copy.Length);
    }

    [Theory]
    [InlineData("properties/00000000-0000-0000-0000-000000000000/documents/missing.pdf")]
    [InlineData("/uploads/properties/missing/doc.pdf")]
    [InlineData("properties/../../outside.pdf")]
    public async Task OpenReadAsync_WithMissingOrInvalidReference_ReturnsNull(string reference)
    {
        Assert.Null(await _service.OpenReadAsync(reference));
    }

    [Fact]
    public async Task DeleteDocumentAsync_WithUploadedDocument_DeletesObject()
    {
        var reference = await _service.UploadDocumentAsync(CreateFormFile("doc.pdf", "application/pdf", 100), Guid.NewGuid());

        await _service.DeleteDocumentAsync(reference);

        Assert.False(await _storage.ExistsAsync(StorageBucket.Private, reference));
    }

    [Fact]
    public async Task DeleteDocumentAsync_WithLegacyPath_DoesNotThrow()
    {
        await _service.DeleteDocumentAsync("/uploads/properties/x/documents/doc.pdf");
    }

    [Fact]
    public async Task GetDocumentSignedUrlAsync_WithFileSystemProvider_ReturnsNull()
    {
        var reference = await _service.UploadDocumentAsync(CreateFormFile("doc.pdf", "application/pdf", 100), Guid.NewGuid());

        Assert.Null(await _service.GetDocumentSignedUrlAsync(reference, "doc.pdf"));
    }

    [Fact]
    public async Task GetDocumentSignedUrlAsync_WithSigningProvider_ReturnsUrlWithConfiguredLifetime()
    {
        var mock = new Mock<IFileStorage>();
        var signed = new Uri("https://storage.test/signed?X-Amz-Signature=abc");
        mock.Setup(s => s.GetSignedReadUrlAsync("properties/p/documents/d.pdf", TimeSpan.FromMinutes(7), "Contratto.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(signed);
        var service = new ImageStorageService(
            mock.Object, Options.Create(new StorageOptions { SignedUrlTtlMinutes = 7 }), NullLogger<ImageStorageService>.Instance);

        var result = await service.GetDocumentSignedUrlAsync("properties/p/documents/d.pdf", "Contratto.pdf");

        Assert.NotNull(result);
        Assert.Equal(signed, result!.Url);
        Assert.InRange(result.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(6), DateTime.UtcNow.AddMinutes(7));
    }

    private static IFormFile CreateFormFile(string fileName, string contentType, long length)
    {
        var content = new byte[length];
        Random.Shared.NextBytes(content);
        return new FormFile(new MemoryStream(content), 0, length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
