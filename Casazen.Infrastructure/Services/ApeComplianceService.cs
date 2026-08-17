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

        foreach (var ape in apeDocs)
        {
            await using var stream = await storage.OpenReadAsync(ape.StorageUrl);
            if (stream is null)
                continue;
            if (inspector.Inspect(stream).IsValid)
                return;
        }

        throw ApeComplianceException.InvalidContent();
    }

    public async Task EnsureUploadedFileIsOfficialApeAsync(IFormFile file)
    {
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        if (!inspector.Inspect(buffer).IsValid)
            throw ApeComplianceException.InvalidContent();
    }
}
