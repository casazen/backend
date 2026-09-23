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
}
