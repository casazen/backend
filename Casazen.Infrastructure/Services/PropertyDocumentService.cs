using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyDocumentService(
    IPropertyDocumentRepository documentRepository,
    IImageStorageService storageService,
    IPropertyRepository propertyRepository,
    ILogger<PropertyDocumentService> logger) : IPropertyDocumentService
{
    public async Task<PropertyDocument> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy)
    {
        var exists = await propertyRepository.ExistsAsync(propertyId);
        if (!exists)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        var storageUrl = await storageService.UploadImageAsync(file, propertyId);

        try
        {
            var document = new PropertyDocument
            {
                PropertyId = propertyId,
                FileName = file.FileName,
                StorageUrl = storageUrl,
                DocumentType = documentType,
                UploadedBy = uploadedBy,
                UploadedAt = DateTime.UtcNow
            };

            logger.LogInformation("Uploading document {FileName} of type {DocumentType} for property {PropertyId} by {UploadedBy}",
                file.FileName, documentType, propertyId, uploadedBy);

            return await documentRepository.AddAsync(document);
        }
        catch
        {
            logger.LogWarning("DB save failed after storage upload for property {PropertyId}; rolling back storage file {StorageUrl}",
                propertyId, storageUrl);
            await storageService.DeleteImageAsync(storageUrl);
            throw;
        }
    }

    public async Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId)
    {
        return await documentRepository.GetByPropertyIdAsync(propertyId);
    }

    public async Task DeleteDocumentAsync(Guid documentId)
    {
        var document = await documentRepository.GetByIdAsync(documentId);
        if (document == null)
        {
            throw new InvalidOperationException($"Document {documentId} not found");
        }

        logger.LogInformation("Deleting document {DocumentId} with storage URL {StorageUrl}", documentId, document.StorageUrl);

        await documentRepository.DeleteAsync(documentId);
        await storageService.DeleteImageAsync(document.StorageUrl);
    }
}
