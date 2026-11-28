using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IPropertyDocumentService
{
    Task<PropertyDocumentUpload> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy);
    Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId);
    Task<PropertyDocument?> GetDocumentAsync(Guid documentId);
    Task DeleteDocumentAsync(Guid documentId);

    Task<Stream?> OpenContentAsync(PropertyDocument document);

    Task<SignedFileUrl?> GetSignedDownloadUrlAsync(PropertyDocument document);

    Task<PropertyDocument> UpdateApeIdentificationAsync(PropertyDocument document, string? code, string? energyClass);

    Task<PropertyDocumentUpload> ReadApeIdentificationAsync(PropertyDocument document);
}

public sealed record PropertyDocumentUpload(PropertyDocument Document, ApeUploadCheck? ApeCheck);
