using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IPropertyDocumentService
{
    /// <summary>Stores a document; for an APE, also what the automatic reading found (<see cref="PropertyDocumentUpload.ApeCheck"/>).</summary>
    Task<PropertyDocumentUpload> UploadDocumentAsync(Guid propertyId, IFormFile file, DocumentType documentType, string uploadedBy);
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

    /// <summary>
    /// Reads the code and the energy class of a stored APE again (one uploaded before they were read, or whose reading found
    /// nothing): the OCR of the file and, for a code it does not show, the public register of the region. Only empty values are
    /// filled; a code or a class typed by the landlord stays. 422 <c>document_not_ape</c> for another document type.
    /// </summary>
    Task<PropertyDocumentUpload> ReadApeIdentificationAsync(PropertyDocument document);
}

/// <summary>A document just stored and, for an APE, what its automatic reading found (null for any other type).</summary>
public sealed record PropertyDocumentUpload(PropertyDocument Document, ApeUploadCheck? ApeCheck);
