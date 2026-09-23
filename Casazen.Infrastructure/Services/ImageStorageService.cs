using Casazen.Core.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Property photos, supplier photos and property documents on <see cref="IFileStorage"/>
/// (FD-07: object storage instead of the ephemeral container disk).
/// </summary>
public class ImageStorageService(
    IFileStorage storage,
    IOptions<StorageOptions> options,
    ILogger<ImageStorageService> logger) : IImageStorageService
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] AllowedMimeTypes = ["image/jpeg", "image/png", "image/webp"];
    private static readonly string[] AllowedDocumentExtensions = [".pdf", ".doc", ".docx", ".jpg", ".jpeg", ".png"];
    private static readonly string[] AllowedDocumentMimeTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/jpeg",
        "image/png",
    ];

    public Task<string> UploadImageAsync(IFormFile file, Guid propertyId) =>
        UploadPublicImageAsync(file, StorageKeys.PropertyPhoto(propertyId, StorageKeys.NewFileName(file?.FileName)), propertyId);

    public Task<string> UploadSupplierPhotoAsync(IFormFile file, Guid supplierOrgId) =>
        UploadPublicImageAsync(file, StorageKeys.SupplierPhoto(supplierOrgId, StorageKeys.NewFileName(file?.FileName)), supplierOrgId);

    public async Task DeleteImageAsync(string imageUrl)
    {
        var key = storage.TryGetPublicKey(imageUrl);
        if (key is null)
        {
            logger.LogWarning("Image URL is not managed by the object storage; nothing deleted: {ImageUrl}", imageUrl);
            return;
        }

        await storage.DeleteAsync(StorageBucket.Public, key);
    }

    public bool ValidateImage(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            logger.LogWarning("Image validation failed: file is null or empty");
            return false;
        }

        if (file.Length > MaxFileSizeBytes)
        {
            logger.LogWarning("Image validation failed: file size {Size} exceeds limit {Limit}", file.Length, MaxFileSizeBytes);
            return false;
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            logger.LogWarning("Image validation failed: invalid extension {Extension}", extension);
            return false;
        }

        if (!AllowedMimeTypes.Contains(file.ContentType.ToLowerInvariant()))
        {
            logger.LogWarning("Image validation failed: invalid MIME type {MimeType}", file.ContentType);
            return false;
        }

        return true;
    }

    public async Task<string> UploadDocumentAsync(IFormFile file, Guid propertyId)
    {
        if (!ValidateDocument(file))
            throw new InvalidOperationException("Invalid document file");

        var key = StorageKeys.PropertyDocument(propertyId, StorageKeys.NewFileName(file.FileName));
        try
        {
            await using var content = file.OpenReadStream();
            await storage.PutAsync(StorageBucket.Private, key, content, file.ContentType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload document for property {PropertyId}", propertyId);
            throw;
        }

        logger.LogInformation("Document uploaded for property {PropertyId}: {Key}", propertyId, key);
        return key;
    }

    public async Task DeleteDocumentAsync(string storageReference)
    {
        if (!StorageKeys.IsValid(storageReference))
        {
            logger.LogWarning("Document reference is not a storage key (legacy path?); nothing deleted: {Reference}", storageReference);
            return;
        }

        await storage.DeleteAsync(StorageBucket.Private, storageReference);
    }

    public bool ValidateDocument(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            logger.LogWarning("Document validation failed: file is null or empty");
            return false;
        }

        if (file.Length > MaxFileSizeBytes)
        {
            logger.LogWarning("Document validation failed: file size {Size} exceeds limit {Limit}", file.Length, MaxFileSizeBytes);
            return false;
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedDocumentExtensions.Contains(extension))
        {
            logger.LogWarning("Document validation failed: invalid extension {Extension}", extension);
            return false;
        }

        if (!AllowedDocumentMimeTypes.Contains(file.ContentType.ToLowerInvariant()))
        {
            logger.LogWarning("Document validation failed: invalid MIME type {MimeType}", file.ContentType);
            return false;
        }

        return true;
    }

    public Task<Stream?> OpenReadAsync(string storageReference) =>
        StorageKeys.IsValid(storageReference)
            ? storage.OpenReadAsync(StorageBucket.Private, storageReference)
            : Task.FromResult<Stream?>(null);

    public async Task<SignedFileUrl?> GetDocumentSignedUrlAsync(string storageReference, string downloadFileName)
    {
        if (!StorageKeys.IsValid(storageReference))
            return null;

        var lifetime = TimeSpan.FromMinutes(options.Value.SignedUrlTtlMinutes);
        var expiresAt = DateTime.UtcNow.Add(lifetime);
        var url = await storage.GetSignedReadUrlAsync(storageReference, lifetime, downloadFileName);
        return url is null ? null : new SignedFileUrl(url, expiresAt);
    }

    private async Task<string> UploadPublicImageAsync(IFormFile file, string key, Guid ownerId)
    {
        if (!ValidateImage(file))
            throw new InvalidOperationException("Invalid image file");

        try
        {
            await using var content = file.OpenReadStream();
            await storage.PutAsync(StorageBucket.Public, key, content, file.ContentType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload image for {OwnerId}", ownerId);
            throw;
        }

        logger.LogInformation("Image uploaded for {OwnerId}: {Key}", ownerId, key);
        return storage.GetPublicUrl(key);
    }
}
