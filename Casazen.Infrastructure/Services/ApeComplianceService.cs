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

        ApeInspectionResult? failure = null;
        foreach (var ape in apeDocs)
        {
            await using var stream = await storage.OpenReadAsync(ape.StorageUrl);
            if (stream is null)
                continue;
            var result = await inspector.InspectAsync(stream);
            if (result.IsValid) return;
            if (failure?.Status != ApeInspectionStatus.OcrUnavailable) failure = result;
        }

        throw failure is null ? ApeComplianceException.InvalidContent() : new ApeComplianceException(failure);
    }

    public async Task EnsureUploadedFileIsOfficialApeAsync(IFormFile file)
    {
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        var result = await inspector.InspectAsync(buffer);
        if (!result.IsValid)
            throw new ApeComplianceException(result);
    }
}
