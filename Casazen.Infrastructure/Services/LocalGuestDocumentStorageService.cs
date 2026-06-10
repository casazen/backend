using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class LocalGuestDocumentStorageService(
    IConfiguration configuration,
    ILogger<LocalGuestDocumentStorageService> logger) : IGuestDocumentStorage
{
    private readonly string _storagePath = configuration["GuestDocumentStorage:LocalPath"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads", "guest-documents");
    private readonly string _baseUrl = configuration["GuestDocumentStorage:BaseUrl"]
        ?? "/uploads/guest-documents";
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".pdf"];
    private static readonly string[] AllowedMimeTypes =
    [
        "image/jpeg",
        "image/png",
        "application/pdf",
    ];

    public async Task<string> UploadDocumentAsync(IFormFile file, Guid orgId, Guid guestId)
    {
        if (!ValidateDocument(file))
            throw new InvalidOperationException("Invalid document file");

        var guestDir = Path.Combine(_storagePath, orgId.ToString(), guestId.ToString());
        Directory.CreateDirectory(guestDir);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(guestDir, fileName);

        try
        {
            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);
            logger.LogInformation(
                "Guest document uploaded for org {OrgId}, guest {GuestId}",
                orgId, guestId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload guest document for guest {GuestId}", guestId);
            throw;
        }

        return $"{_baseUrl}/{orgId}/{guestId}/{fileName}";
    }

    public bool ValidateDocument(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            logger.LogWarning("Guest document validation failed: file is null or empty");
            return false;
        }

        if (file.Length > MaxFileSizeBytes)
        {
            logger.LogWarning("Guest document validation failed: file size {Size} exceeds 5MB limit", file.Length);
            return false;
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            logger.LogWarning("Guest document validation failed: invalid extension {Extension}", extension);
            return false;
        }

        if (!AllowedMimeTypes.Contains(file.ContentType.ToLowerInvariant()))
        {
            logger.LogWarning("Guest document validation failed: invalid MIME type {MimeType}", file.ContentType);
            return false;
        }

        return true;
    }
}
