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

    /// <summary>
    /// PROOF TEST: privacy rule (E20/F02/US01/T02, NF-121). The provider that really deletes writes the key of a guest's document (<c>guest-documents/{org}/{guest}/…</c>, the
    /// ids of the org and of the guest) in no line of its log, at no level: it says that an object of the guests' documents was deleted; the key of another object is logged as before.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_TheKeyOfAGuestDocument_IsNeverWrittenInTheLog_Test()
    {
        var log = new CapturingLogger<FileSystemFileStorage>();
        var storage = new FileSystemFileStorage(
            Options.Create(new StorageOptions
            {
                Provider = StorageOptions.FileSystemProvider,
                PublicBaseUrl = "http://localhost:5100/storage/public",
                FileSystem = new FileSystemStorageOptions { RootPath = _root },
            }),
            log);
        var guestKey = StorageKeys.GuestDocument(Guid.NewGuid(), Guid.NewGuid(), $"{Guid.NewGuid():N}.bin");
        await storage.PutAsync(StorageBucket.Private, guestKey, new MemoryStream([1, 2]), "application/octet-stream");
        await storage.PutAsync(StorageBucket.Private, "properties/p/documents/a.pdf", new MemoryStream([3]), "application/pdf");
        log.Lines.Clear();

        await storage.DeleteAsync(StorageBucket.Private, guestKey);
        await storage.DeleteAsync(StorageBucket.Private, "properties/p/documents/a.pdf");

        Assert.False(await storage.ExistsAsync(StorageBucket.Private, guestKey));
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains(StorageKeys.GuestDocumentPrefix, StringComparison.Ordinal) || line.Contains(guestKey, StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("guests' documents", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("properties/p/documents/a.pdf", StringComparison.Ordinal));
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
