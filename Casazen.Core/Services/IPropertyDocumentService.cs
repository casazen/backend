using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IPropertyDocumentService
{
    Task<PropertyDocument> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy);
    Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId);
    Task<PropertyDocument?> GetDocumentAsync(Guid documentId);
    Task DeleteDocumentAsync(Guid documentId);

    /// <summary>Opens the stored file of a document, or null when the file is missing from the storage.</summary>
    Task<Stream?> OpenContentAsync(PropertyDocument document);

    /// <summary>Short-lived signed download URL of a document, or null when the storage cannot sign URLs.</summary>
    Task<SignedFileUrl?> GetSignedDownloadUrlAsync(PropertyDocument document);

    /// <summary>
    /// Sets the code and energy class printed on an APE document (LT-10): the lease contract states them. 422
    /// <c>document_not_ape</c> for another document type, <c>ape_identification_invalid</c> for an empty code or a class
    /// that is not 1-3 letters, digits or "+".
    /// </summary>
    Task<PropertyDocument> UpdateApeIdentificationAsync(PropertyDocument document, string? code, string? energyClass);
}
