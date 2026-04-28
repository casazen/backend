using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class LocalImageStorageServiceTests : IDisposable
{
    private readonly LocalImageStorageService _service;
    private readonly string _testStoragePath;
    private readonly Mock<IConfiguration> _mockConfiguration;

    public LocalImageStorageServiceTests()
    {
        _testStoragePath = Path.Combine(Path.GetTempPath(), "casazen-test-uploads", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testStoragePath);

        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(x => x["ImageStorage:LocalPath"]).Returns(_testStoragePath);
        _mockConfiguration.Setup(x => x["ImageStorage:BaseUrl"]).Returns("/uploads/properties");

        _service = new LocalImageStorageService(
            _mockConfiguration.Object,
            new Mock<ILogger<LocalImageStorageService>>().Object
        );
    }

    [Fact]
    public void ValidateImage_WithValidJpeg_ReturnsTrue()
    {
        // Arrange
        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateImage_WithValidPng_ReturnsTrue()
    {
        // Arrange
        var mockFile = CreateMockFormFile("test.png", "image/png", 2048);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateImage_WithValidWebP_ReturnsTrue()
    {
        // Arrange
        var mockFile = CreateMockFormFile("test.webp", "image/webp", 1500);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateImage_WithInvalidExtension_ReturnsFalse()
    {
        // Arrange
        var mockFile = CreateMockFormFile("test.txt", "text/plain", 1024);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateImage_WithInvalidMimeType_ReturnsFalse()
    {
        // Arrange - File has .jpg extension but wrong MIME type
        var mockFile = CreateMockFormFile("test.jpg", "text/plain", 1024);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateImage_WithOversizedFile_ReturnsFalse()
    {
        // Arrange - 11MB file (over 10MB limit)
        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 11 * 1024 * 1024);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateImage_WithNullFile_ReturnsFalse()
    {
        // Act
        var result = _service.ValidateImage(null!);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateImage_WithEmptyFile_ReturnsFalse()
    {
        // Arrange
        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 0);

        // Act
        var result = _service.ValidateImage(mockFile);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task UploadImageAsync_WithValidFile_CreatesFileAndReturnsUrl()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

        // Act
        var url = await _service.UploadImageAsync(mockFile, propertyId);

        // Assert
        Assert.NotNull(url);
        Assert.StartsWith("/uploads/properties/", url);
        Assert.Contains(propertyId.ToString(), url);

        // Verify file was created
        var expectedDir = Path.Combine(_testStoragePath, propertyId.ToString());
        Assert.True(Directory.Exists(expectedDir));
        Assert.NotEmpty(Directory.GetFiles(expectedDir));
    }

    [Fact]
    public async Task UploadImageAsync_WithInvalidFile_ThrowsException()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFormFile("test.txt", "text/plain", 1024);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UploadImageAsync(mockFile, propertyId));
    }

    [Fact]
    public async Task DeleteImageAsync_WithExistingFile_DeletesFile()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

        // Upload file first
        var url = await _service.UploadImageAsync(mockFile, propertyId);

        // Verify file exists
        var expectedDir = Path.Combine(_testStoragePath, propertyId.ToString());
        var filesBefore = Directory.GetFiles(expectedDir);
        Assert.NotEmpty(filesBefore);

        // Act
        await _service.DeleteImageAsync(url);

        // Assert
        var filesAfter = Directory.GetFiles(expectedDir);
        Assert.Empty(filesAfter);
    }

    [Fact]
    public async Task DeleteImageAsync_WithNonExistentFile_DoesNotThrow()
    {
        // Arrange
        var nonExistentUrl = "/uploads/properties/nonexistent/file.jpg";

        // Act & Assert - Should not throw
        await _service.DeleteImageAsync(nonExistentUrl);
    }

    private IFormFile CreateMockFormFile(string fileName, string contentType, long length)
    {
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.ContentType).Returns(contentType);
        mockFile.Setup(f => f.Length).Returns(length);

        // Create a memory stream with dummy data
        var content = new byte[length];
        var stream = new MemoryStream(content);
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream target, CancellationToken token) =>
            {
                stream.Position = 0;
                return stream.CopyToAsync(target, token);
            });

        return mockFile.Object;
    }

    public void Dispose()
    {
        // Cleanup test storage directory
        if (Directory.Exists(_testStoragePath))
        {
            Directory.Delete(_testStoragePath, true);
        }
    }
}
