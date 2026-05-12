using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IPropertyDocumentService
{
    Task<PropertyDocument> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy);
    Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId);
    Task DeleteDocumentAsync(Guid documentId);
}
