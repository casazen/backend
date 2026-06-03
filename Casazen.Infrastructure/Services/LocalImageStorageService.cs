using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class LocalImageStorageService : IImageStorageService
{
    private readonly ILogger<LocalImageStorageService> _logger;
    private readonly string _storagePath;
    private readonly string _baseUrl;
    private readonly long _maxFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private readonly string[] _allowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
    private readonly string[] _allowedMimeTypes = { "image/jpeg", "image/png", "image/webp" };

    public LocalImageStorageService(
        IConfiguration configuration,
        ILogger<LocalImageStorageService> logger)
    {
        _logger = logger;
        _storagePath = configuration["ImageStorage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "properties");
        _baseUrl = configuration["ImageStorage:BaseUrl"] ?? "/uploads/properties";

        // Ensure storage directory exists
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
            _logger.LogInformation("Created image storage directory: {Path}", _storagePath);
        }
    }

    public async Task<string> UploadImageAsync(IFormFile file, Guid propertyId)
    {
        if (!ValidateImage(file))
        {
            throw new InvalidOperationException("Invalid image file");
        }

        // Create property-specific directory
        var propertyDir = Path.Combine(_storagePath, propertyId.ToString());
        if (!Directory.Exists(propertyDir))
        {
            Directory.CreateDirectory(propertyDir);
        }

        // Generate unique filename
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(propertyDir, fileName);

        // Save file
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);
            _logger.LogInformation("Image uploaded: {FileName} for property {PropertyId}", fileName, propertyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload image for property {PropertyId}", propertyId);
            throw;
        }

        // Return public URL
        return $"{_baseUrl}/{propertyId}/{fileName}";
    }

    public Task DeleteImageAsync(string imageUrl)
    {
        try
        {
            // Extract file path from URL
            var relativePath = imageUrl.Replace(_baseUrl, "").TrimStart('/');
            var filePath = Path.Combine(_storagePath, relativePath);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Image deleted: {FilePath}", filePath);
            }
            else
            {
                _logger.LogWarning("Image file not found for deletion: {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete image: {ImageUrl}", imageUrl);
            throw;
        }

        return Task.CompletedTask;
    }

    public bool ValidateImage(IFormFile file)
    {
        // Check file is not null or empty
        if (file == null || file.Length == 0)
        {
            _logger.LogWarning("Image validation failed: file is null or empty");
            return false;
        }

        // Check file size
        if (file.Length > _maxFileSizeBytes)
        {
            _logger.LogWarning("Image validation failed: file size {Size} exceeds limit {Limit}",
                file.Length, _maxFileSizeBytes);
            return false;
        }

        // Check file extension
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(extension))
        {
            _logger.LogWarning("Image validation failed: invalid extension {Extension}", extension);
            return false;
        }

        // Check MIME type (not just extension)
        if (!_allowedMimeTypes.Contains(file.ContentType.ToLowerInvariant()))
        {
            _logger.LogWarning("Image validation failed: invalid MIME type {MimeType}", file.ContentType);
            return false;
        }

        return true;
    }
}
