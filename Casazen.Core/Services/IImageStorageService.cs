using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IImageStorageService
{
    /// <summary>
    /// Upload an image and return the public URL
    /// </summary>
    /// <param name="file">Image file to upload</param>
    /// <param name="propertyId">Property ID for organizing files</param>
    /// <returns>Public URL of uploaded image</returns>
    Task<string> UploadImageAsync(IFormFile file, Guid propertyId);

    /// <summary>
    /// Delete an image by its URL
    /// </summary>
    /// <param name="imageUrl">URL of the image to delete</param>
    Task DeleteImageAsync(string imageUrl);

    /// <summary>
    /// Validate if a file is a valid image
    /// </summary>
    /// <param name="file">File to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    bool ValidateImage(IFormFile file);

    /// <summary>
    /// Upload a compliance document and return the public URL
    /// </summary>
    Task<string> UploadDocumentAsync(IFormFile file, Guid propertyId);

    /// <summary>
    /// Validate if a file is an accepted compliance document (PDF, DOC, DOCX, JPG, PNG)
    /// </summary>
    bool ValidateDocument(IFormFile file);

    /// <summary>
    /// Opens a previously uploaded file for reading, or null if it is missing.
    /// </summary>
    Task<Stream?> OpenReadAsync(string storageUrl);
}
