using Casazen.Core.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure.Storage;

/// <summary>FD-07: the Development/Testing provider keeps buckets apart and never escapes its root.</summary>
public class FileSystemFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "casazen-fs-storage", Guid.NewGuid().ToString("N"));
    private readonly FileSystemFileStorage _storage;

    public FileSystemFileStorageTests()
    {
        _storage = new FileSystemFileStorage(
            Options.Create(new StorageOptions
            {
                Provider = StorageOptions.FileSystemProvider,
                PublicBaseUrl = "http://localhost:5100/storage/public",
                FileSystem = new FileSystemStorageOptions { RootPath = _root },
            }),
            NullLogger<FileSystemFileStorage>.Instance);
    }

    [Fact]
    public async Task PutAsync_PrivateObject_IsWrittenOutsideThePublicFolder()
    {
        await _storage.PutAsync(StorageBucket.Private, "guest-documents/o/g/id.pdf", new MemoryStream([1, 2]), "application/pdf");

        Assert.True(File.Exists(Path.Combine(_root, "private", "guest-documents", "o", "g", "id.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_root, "public")));
        Assert.False(await _storage.ExistsAsync(StorageBucket.Public, "guest-documents/o/g/id.pdf"));
    }

    [Fact]
    public async Task PutAsync_Overwrite_ReplacesContent()
    {
        await _storage.PutAsync(StorageBucket.Public, "properties/p/photos/a.jpg", new MemoryStream([1, 2, 3]), "image/jpeg");
        await _storage.PutAsync(StorageBucket.Public, "properties/p/photos/a.jpg", new MemoryStream([4]), "image/jpeg");

        await using var stream = await _storage.OpenReadAsync(StorageBucket.Public, "properties/p/photos/a.jpg");
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        Assert.Equal(new byte[] { 4 }, copy.ToArray());
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("properties/../../outside.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("properties\\..\\x.pdf")]
    public async Task PutAsync_TraversalKey_Throws(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.PutAsync(StorageBucket.Private, key, new MemoryStream([1]), "application/pdf"));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "outside.pdf")));
    }

    [Fact]
    public async Task GetSignedReadUrlAsync_IsNotSupported_ReturnsNull()
    {
        Assert.Null(await _storage.GetSignedReadUrlAsync("properties/p/documents/a.pdf", TimeSpan.FromMinutes(5), "a.pdf"));
    }

    [Fact]
    public void GetPublicUrl_ReturnsAbsoluteUrlServedByTheApi()
    {
        Assert.Equal(
            "http://localhost:5100/storage/public/properties/p/photos/a.jpg",
            _storage.GetPublicUrl("properties/p/photos/a.jpg"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
