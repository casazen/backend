using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace Casazen.Infrastructure.Services;

public sealed class ApeComplianceService(
    IPropertyDocumentRepository documents,
    IImageStorageService storage,
    IApeDocumentInspector inspector,
    IApeRegistryLookup? registry = null,
    TimeProvider? timeProvider = null) : IApeComplianceService
{
    /// <summary>The CasaZen code of Lombardy, whose register (CENED) is public.</summary>
    private const string LombardyRegionCode = "LOM";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

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

    public async Task<ApeUploadCheck> CheckUploadedApeAsync(IFormFile file, ApeRegistryContext? context = null)
    {
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        var result = await inspector.InspectAsync(buffer);
        // Not a PDF or an image, a broken file, a certificate past its date: nothing a confirmation could fix.
        if (result.Status is ApeInspectionStatus.NotAPdf or ApeInspectionStatus.Unreadable or ApeInspectionStatus.Expired)
            throw new ApeComplianceException(result);

        // Read but not whole, not recognised, or not read because the reader is down: the file is kept and the landlord confirms it
        // with its code and energy class. Refusing it left a real certificate with no way in (a photocopy whose header is too dark to
        // read, a regional model with other labels).
        var check = await DescribeAsync(result, context, CancellationToken.None);
        return check.Outcome == ApeUploadCheck.Expired
            ? throw new ApeComplianceException(new ApeInspectionResult(ApeInspectionStatus.Expired))
            : check;
    }

    public async Task<ApeUploadCheck> ReadStoredApeAsync(Stream content, ApeRegistryContext? context = null, CancellationToken cancellationToken = default)
    {
        var result = await inspector.InspectAsync(content, cancellationToken);
        return result.Status is ApeInspectionStatus.NotAPdf or ApeInspectionStatus.Unreadable
            ? new ApeUploadCheck(ApeUploadCheck.NotRecognized, [])
            : await DescribeAsync(result, context, cancellationToken);
    }

    /// <summary>
    /// The outcome of a reading and the code and class it gave. A code that is not on the file (or not legible) is asked to the
    /// register when the certificate is a Lombard one and its index was read: the register's code is taken only if its class is the
    /// one read on the file (when one was), and then the code is no longer "not read".
    /// </summary>
    private async Task<ApeUploadCheck> DescribeAsync(ApeInspectionResult result, ApeRegistryContext? context, CancellationToken cancellationToken)
    {
        var outcome = result.Status switch
        {
            ApeInspectionStatus.Valid => ApeUploadCheck.Verified,
            ApeInspectionStatus.Incomplete => ApeUploadCheck.Incomplete,
            ApeInspectionStatus.Expired => ApeUploadCheck.Expired,
            ApeInspectionStatus.OcrUnavailable => ApeUploadCheck.NotChecked,
            _ => ApeUploadCheck.NotRecognized,
        };
        var reading = result.Reading;
        var unread = (IReadOnlyList<string>)(result.MissingFields ?? []);
        var code = reading.CertificateCode;
        var source = code is null ? null : ApeUploadCheck.FromDocument;
        var energyClass = reading.EnergyClass;

        if (code is null && registry is not null && outcome != ApeUploadCheck.NotChecked
            && context?.Comune is { Length: > 0 } comune && reading.GlobalNonRenewableIndex is decimal index && index > 0
            && (reading.LombardyModel || string.Equals(context.RegionCode, LombardyRegionCode, StringComparison.OrdinalIgnoreCase)))
        {
            var entry = await registry.FindAsync(new ApeRegistryQuery(comune, index, reading.IssueDate), cancellationToken);
            if (entry is not null && (energyClass is null || entry.EnergyClass is null || entry.EnergyClass == energyClass))
            {
                code = entry.Code;
                source = ApeUploadCheck.FromRegistry;
                energyClass ??= entry.EnergyClass;
                unread = [.. unread.Where(field => field != "CertificateCode" && !(field == "EnergyClass" && energyClass is not null))];
                if (outcome == ApeUploadCheck.Incomplete && unread.Count == 0)
                    outcome = reading.ExpiryDate is { } expiry && expiry < _clock.TodayInRomeAsDateOnly()
                        ? ApeUploadCheck.Expired
                        : ApeUploadCheck.Verified;
            }
        }

        return new ApeUploadCheck(outcome, unread) { CertificateCode = code, EnergyClass = energyClass, CodeSource = source };
    }
}
