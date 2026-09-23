using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Guest ID document scans on the PRIVATE bucket of <see cref="IFileStorage"/> (FD-07). The value
/// returned (and stored in <c>Guest.DocumentScanUrl</c>) is the storage key, never a public URL.
/// </summary>
public class GuestDocumentStorageService(
    IFileStorage storage,
    ILogger<GuestDocumentStorageService> logger) : IGuestDocumentStorage
{
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

        var key = StorageKeys.GuestDocument(orgId, guestId, StorageKeys.NewFileName(file.FileName));
        try
        {
            await using var content = file.OpenReadStream();
            await storage.PutAsync(StorageBucket.Private, key, content, file.ContentType);
            logger.LogInformation(
                "Guest document uploaded for org {OrgId}, guest {GuestId}",
                orgId, guestId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload guest document for guest {GuestId}", guestId);
            throw;
        }

        return key;
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
