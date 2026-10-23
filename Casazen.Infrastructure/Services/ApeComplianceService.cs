using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Casazen.Infrastructure.Services;

public sealed class ApeComplianceService(
    IPropertyDocumentRepository documents,
    IImageStorageService storage,
    IApeDocumentInspector inspector) : IApeComplianceService
{
    public async Task EnsurePropertyHasValidApeAsync(Guid propertyId)
    {
        var apeDocs = (await documents.GetByPropertyIdAsync(propertyId))
            .Where(d => d.DocumentType == DocumentType.Ape)
            .ToList();

        if (apeDocs.Count == 0)
            throw ApeComplianceException.Required();

        // The code and the energy class typed by the landlord (LT-10) are what the contract prints, and they confirm a certificate the
        // automatic reading could not read whole (a photo, a photocopy whose header is too dark): no file to read again, nothing to wait for.
        if (apeDocs.Any(d => d.HasApeIdentification))
            return;

        var read = new List<ApeInspectionResult>();
        foreach (var ape in apeDocs)
        {
            await using var stream = await storage.OpenReadAsync(ape.StorageUrl);
            if (stream is null)
                continue;
            var result = await inspector.InspectAsync(stream);
            if (result.IsValid) return;
            read.Add(result);
        }

        if (read.Count == 0)
            throw ApeComplianceException.InvalidContent();
        // Only certificates past their date: a new one is needed. Any other one the landlord confirms by typing its code and class.
        if (read.TrueForAll(result => result.Status == ApeInspectionStatus.Expired))
            throw new ApeComplianceException(read[0]);
        throw ApeComplianceException.IdentificationRequired();
    }

    public async Task<ApeUploadCheck> CheckUploadedApeAsync(IFormFile file)
    {
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        var result = await inspector.InspectAsync(buffer);
        return result.Status switch
        {
            ApeInspectionStatus.Valid => ApeUploadCheck.Passed,
            // Read but not whole, not recognised, or not read because the reader is down: the file is kept and the landlord confirms
            // it with its code and energy class. Refusing it left a real certificate with no way in (a photocopy whose header is too
            // dark to read, a regional model with other labels).
            ApeInspectionStatus.Incomplete => new ApeUploadCheck(ApeUploadCheck.Incomplete, result.MissingFields ?? []),
            ApeInspectionStatus.ContentMismatch => new ApeUploadCheck(ApeUploadCheck.NotRecognized, []),
            ApeInspectionStatus.OcrUnavailable => new ApeUploadCheck(ApeUploadCheck.NotChecked, []),
            // Not a PDF or an image, a broken file, a certificate past its date: nothing a confirmation could fix.
            _ => throw new ApeComplianceException(result),
        };
    }
}
