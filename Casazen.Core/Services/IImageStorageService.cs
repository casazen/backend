using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>
/// Property and supplier files on <see cref="IFileStorage"/>: photos go to the public bucket and are
/// referenced by their absolute public URL; property documents go to the private bucket and are
/// referenced by their storage key (never a URL: they are downloaded through an authenticated endpoint).
/// </summary>
public interface IImageStorageService
{
    /// <summary>Uploads a property photo and returns its absolute public URL.</summary>
    Task<string> UploadImageAsync(IFormFile file, Guid propertyId);

    /// <summary>Uploads a supplier photo and returns its absolute public URL.</summary>
    Task<string> UploadSupplierPhotoAsync(IFormFile file, Guid supplierOrgId);

    /// <summary>
    /// Deletes a photo returned by <see cref="UploadImageAsync"/> or <see cref="UploadSupplierPhotoAsync"/>.
    /// URLs not issued by the storage (legacy relative paths, external URLs) are left alone.
    /// </summary>
    Task DeleteImageAsync(string imageUrl);

    /// <summary>True for a JPEG/PNG/WebP image of at most 10 MB.</summary>
    bool ValidateImage(IFormFile file);

    /// <summary>Uploads a compliance document to the private bucket and returns its storage reference.</summary>
    Task<string> UploadDocumentAsync(IFormFile file, Guid propertyId);

    /// <summary>Deletes a document by its storage reference; unknown or legacy references are left alone.</summary>
    Task DeleteDocumentAsync(string storageReference);

    /// <summary>True for an accepted compliance document (PDF, DOC, DOCX, JPG, PNG) of at most 10 MB.</summary>
    bool ValidateDocument(IFormFile file);

    /// <summary>Opens a previously uploaded document for reading, or null if it is missing.</summary>
    Task<Stream?> OpenReadAsync(string storageReference);

    /// <summary>
    /// Short-lived signed URL of a document, or null when the storage provider cannot sign URLs
    /// or the reference is not a valid storage key.
    /// </summary>
    Task<SignedFileUrl?> GetDocumentSignedUrlAsync(string storageReference, string downloadFileName);
}

/// <summary>A signed, time-limited URL of a private object.</summary>
public sealed record SignedFileUrl(Uri Url, DateTime ExpiresAtUtc);
