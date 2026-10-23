using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <remarks>
/// Uploading or deleting a document re-evaluates the compliance status of the property (CO-06): an active property whose
/// required document is deleted is suspended from the booking site, a suspended one is reactivated by the upload that
/// completes its requirements.
/// </remarks>
public class PropertyDocumentService(
    IPropertyDocumentRepository documentRepository,
    IImageStorageService storageService,
    IPropertyRepository propertyRepository,
    IApeComplianceService apeCompliance,
    IPropertyComplianceStatusService complianceStatus,
    ILogger<PropertyDocumentService> logger) : IPropertyDocumentService
{
    public async Task<PropertyDocumentUpload> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy)
    {
        var orgId = await propertyRepository.GetOrgIdAsync(propertyId);
        if (orgId is null)
        {
            throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
        }

        if (!storageService.ValidateDocument(file))
        {
            throw new DomainRuleException("invalid_document_file", "DocumentFileInvalid");
        }

        ApeUploadCheck? apeCheck = null;
        if (documentType == DocumentType.Ape)
        {
            // Legacy APE clients also persist a PDF. Other legacy APIs keep their existing file contract;
            // the shared multi-page frontend sends a single assembled PDF for every document type.
            file = await PhotoDocumentPdf.NormalizeAsync(file);
            if (!storageService.ValidateDocument(file))
                throw new DomainRuleException("invalid_document_file", "DocumentFileInvalid");
            apeCheck = await apeCompliance.CheckUploadedApeAsync(file, await RegistryContextAsync(propertyId));
        }

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
            // The code and the class read on the APE (or its code from the register): the contract states them, nobody has to type them.
            if (apeCheck is not null)
                ApplyReadIdentification(document, apeCheck);

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
        return new PropertyDocumentUpload(saved, apeCheck);
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

    public async Task<PropertyDocument> UpdateApeIdentificationAsync(PropertyDocument document, string? code, string? energyClass)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.DocumentType != DocumentType.Ape)
            throw new DomainRuleException("document_not_ape", "DocumentNotApe");

        // Shape only (no invented pattern on the code, whose format is not documented): a non-empty code within the
        // column, a short class of letters, digits or "+" (A4...G, older A+).
        var normalizedCode = code?.Trim();
        var normalizedClass = energyClass?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalizedCode) || normalizedCode.Length > ApeDocumentLimits.CodeMaxLength
            || string.IsNullOrEmpty(normalizedClass) || normalizedClass.Length > ApeDocumentLimits.EnergyClassMaxLength
            || !normalizedClass.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '+'))
        {
            throw new DomainRuleException("ape_identification_invalid", "ApeIdentificationInvalid");
        }

        document.ApeCode = normalizedCode;
        document.ApeEnergyClass = normalizedClass;
        logger.LogInformation("APE identification updated. DocumentId={DocumentId} PropertyId={PropertyId}",
            document.Id, document.PropertyId);
        return await documentRepository.UpdateAsync(document);
    }

    public async Task<PropertyDocumentUpload> ReadApeIdentificationAsync(PropertyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.DocumentType != DocumentType.Ape)
            throw new DomainRuleException("document_not_ape", "DocumentNotApe");

        await using var content = await storageService.OpenReadAsync(document.StorageUrl)
            ?? throw new NotFoundException($"File of document {document.Id} not found") { Code = "document_file_missing", MessageKey = "NotFound" };
        var check = await apeCompliance.ReadStoredApeAsync(content, await RegistryContextAsync(document.PropertyId));

        // Only what is still empty: a code or a class the landlord typed is never replaced by a reading.
        var code = document.ApeCode;
        var energyClass = document.ApeEnergyClass;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(energyClass))
        {
            ApplyReadIdentification(document, check, keep: (code, energyClass));
            if (document.ApeCode != code || document.ApeEnergyClass != energyClass)
            {
                logger.LogInformation("APE identification read from the document. DocumentId={DocumentId} PropertyId={PropertyId} Source={Source}",
                    document.Id, document.PropertyId, check.CodeSource);
                document = await documentRepository.UpdateAsync(document);
            }
        }
        return new PropertyDocumentUpload(document, check);
    }

    /// <summary>The comune and the region of the property: where the register of the APE is asked for a code the file does not show.</summary>
    private async Task<ApeRegistryContext?> RegistryContextAsync(Guid propertyId)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId);
        return property is null ? null : new ApeRegistryContext(property.City, property.RegionCode);
    }

    /// <summary>
    /// Sets the code and the class a reading found, with the shape the hand entry has (LT-10); <paramref name="keep"/> are values
    /// already there, which stay.
    /// </summary>
    private static void ApplyReadIdentification(PropertyDocument document, ApeUploadCheck check, (string? Code, string? EnergyClass) keep = default)
    {
        var code = check.CertificateCode?.Trim();
        if (string.IsNullOrWhiteSpace(keep.Code) && code is { Length: > 0 } && code.Length <= ApeDocumentLimits.CodeMaxLength)
            document.ApeCode = code;
        var energyClass = check.EnergyClass?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(keep.EnergyClass) && energyClass is { Length: > 0 } && energyClass.Length <= ApeDocumentLimits.EnergyClassMaxLength
            && energyClass.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '+'))
            document.ApeEnergyClass = energyClass;
    }
}
