using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>
/// Property and supplier files on <see cref="IFileStorage"/>: photos go to the public bucket and are
/// referenced by their absolute public URL; property documents go to the private bucket and are
/// referenced by their storage key (never a URL: they are downloaded through an authenticated endpoint).
/// </summary>
public interface IImageStorageService
{
    Task<string> UploadImageAsync(IFormFile file, Guid propertyId);

    Task<string> UploadSupplierPhotoAsync(IFormFile file, Guid supplierOrgId);

    Task DeleteImageAsync(string imageUrl);

    bool ValidateImage(IFormFile file);

    Task<string> UploadDocumentAsync(IFormFile file, Guid propertyId);

    Task DeleteDocumentAsync(string storageReference);

    bool ValidateDocument(IFormFile file);

    Task<Stream?> OpenReadAsync(string storageReference);

    Task<SignedFileUrl?> GetDocumentSignedUrlAsync(string storageReference, string downloadFileName);
}

public sealed record SignedFileUrl(Uri Url, DateTime ExpiresAtUtc);
