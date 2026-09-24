using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <remarks>
/// Uploading or deleting a document re-evaluates the compliance status of the property (CO-06): an active property whose
/// required document is deleted is suspended from the booking site.
/// </remarks>
public class PropertyDocumentService(
    IPropertyDocumentRepository documentRepository,
    IImageStorageService storageService,
    IPropertyRepository propertyRepository,
    IApeComplianceService apeCompliance,
    IPropertyComplianceStatusService complianceStatus,
    ILogger<PropertyDocumentService> logger) : IPropertyDocumentService
{
    public async Task<PropertyDocument> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy)
    {
        var orgId = await propertyRepository.GetOrgIdAsync(propertyId);
        if (orgId is null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        if (!storageService.ValidateDocument(file))
        {
            throw new InvalidOperationException("Invalid document file type or size");
        }

        if (documentType == DocumentType.Ape)
            await apeCompliance.EnsureUploadedFileIsOfficialApeAsync(file);

        var storageUrl = await storageService.UploadDocumentAsync(file, propertyId);

        PropertyDocument saved;
        try
        {
            var document = new PropertyDocument
            {
                PropertyId = propertyId,
                OrgId = orgId.Value,
                FileName = file.FileName,
                StorageUrl = storageUrl,
                DocumentType = documentType,
                UploadedBy = uploadedBy,
                UploadedAt = DateTime.UtcNow
            };

            logger.LogInformation("Uploading document {FileName} of type {DocumentType} for property {PropertyId} by {UploadedBy}",
                file.FileName, documentType, propertyId, uploadedBy);

            saved = await documentRepository.AddAsync(document);
        }
        catch
        {
            logger.LogWarning("DB save failed after storage upload for property {PropertyId}; rolling back storage file {StorageUrl}",
                propertyId, storageUrl);
            await storageService.DeleteDocumentAsync(storageUrl);
            throw;
        }

        await complianceStatus.ReevaluateAsync(propertyId);
        return saved;
    }

    public async Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId)
    {
        return await documentRepository.GetByPropertyIdAsync(propertyId);
    }

    public async Task<PropertyDocument?> GetDocumentAsync(Guid documentId)
    {
        return await documentRepository.GetByIdAsync(documentId);
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
        await storageService.DeleteDocumentAsync(document.StorageUrl);
        await complianceStatus.ReevaluateAsync(document.PropertyId);
    }

    public Task<Stream?> OpenContentAsync(PropertyDocument document) =>
        storageService.OpenReadAsync(document.StorageUrl);

    public Task<SignedFileUrl?> GetSignedDownloadUrlAsync(PropertyDocument document) =>
        storageService.GetDocumentSignedUrlAsync(document.StorageUrl, document.FileName);
}
