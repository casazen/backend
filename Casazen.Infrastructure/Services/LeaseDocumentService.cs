using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.BackgroundJobs;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>Original PDFs in the private bucket, metadata owned by the lease and its tenant.</summary>
public sealed class LeaseDocumentService(AppDbContext db, IFileStorage storage, ILogger<LeaseDocumentService> logger) : ILeaseDocumentService
{
    public const long MaxBytes = 20 * 1024 * 1024;

    public async Task<IReadOnlyList<LeaseDocumentDto>> ListAsync(Guid leaseId, CancellationToken ct = default) =>
        await db.LeaseDocuments.AsNoTracking().Where(d => d.LeaseContractId == leaseId)
            .OrderByDescending(d => d.UploadedAt).ThenBy(d => d.Id)
            .Select(d => new LeaseDocumentDto(d.Id, d.LeaseContractId, d.Kind, d.FileName, d.SizeBytes, d.UploadedAt)).ToListAsync(ct);

    public async Task<LeaseDocumentDto> UploadAsync(Guid leaseId, IFormFile file, LeaseDocumentKind kind, string uploadedBy, CancellationToken ct = default)
    {
        // A single photo (JPEG, PNG) of a page is kept as the one-page PDF it becomes, as for the documents of a property: the browser
        // joins several photos in one PDF before it sends them, so the reader of the contracts only ever meets PDFs.
        file = await PhotoDocumentPdf.NormalizeAsync(file);
        var lease = await db.LeaseContracts.AsNoTracking().SingleOrDefaultAsync(l => l.Id == leaseId, ct)
            ?? throw new NotFoundException("Lease not found") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };
        var fileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (file.Length < 5 || file.Length > MaxBytes || !Enum.IsDefined(kind) ||
            fileName.Length is 0 or > 240 || fileName.Any(char.IsControl) ||
            !string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw InvalidPdf();

        // Copy at most MaxBytes+1: neither the multipart length nor the MIME label is trusted.
        await using var input = file.OpenReadStream();
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (content.Length + count > MaxBytes) throw InvalidPdf();
            await content.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (content.Length != file.Length || !content.GetBuffer().AsSpan(0, (int)Math.Min(5, content.Length)).SequenceEqual("%PDF-"u8))
            throw InvalidPdf();
        content.Position = 0;

        // Calculate SHA-256 hash for duplicate detection (ADR-009-R7)
        string documentHash;
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = await sha256.ComputeHashAsync(content, ct);
            documentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        content.Position = 0;

        // Check for duplicate document (ADR-009-R7)
        var existingDocument = await db.LeaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentHash == documentHash && d.LeaseContractId == lease.Id, ct);

        if (existingDocument != null)
        {
            logger.LogInformation(
                "Duplicate document detected: {DocumentId} matches existing document {ExistingId} for lease {LeaseId}",
                documentHash,
                existingDocument.Id,
                lease.Id);

            // Create a new document entry but mark it as skipped
            content.Position = 0;
            var key = StorageKeys.LeaseAttachment(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
            await storage.PutAsync(StorageBucket.Private, key, content, "application/pdf", ct);
            var duplicateDocument = new LeaseDocument
            {
                LeaseContractId = lease.Id,
                OrgId = lease.OrgId,
                Kind = kind,
                FileName = fileName,
                StorageKey = key,
                UploadedBy = uploadedBy,
                SizeBytes = content.Length,
                DocumentHash = documentHash,
                ExtractionStatus = LeaseDocumentExtractionStatus.Skipped,
            };
            try
            {
                db.LeaseDocuments.Add(duplicateDocument);
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                try { await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None); }
                catch (Exception cleanup) { logger.LogWarning(cleanup, "Could not remove orphan lease attachment {DocumentId}", duplicateDocument.Id); }
                throw;
            }
            return new LeaseDocumentDto(duplicateDocument.Id, lease.Id, kind, duplicateDocument.FileName, duplicateDocument.SizeBytes, duplicateDocument.UploadedAt);
        }

        // Upload new document
        content.Position = 0;
        var storageKey = StorageKeys.LeaseAttachment(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        await storage.PutAsync(StorageBucket.Private, storageKey, content, "application/pdf", ct);
        var document = new LeaseDocument
        {
            LeaseContractId = lease.Id,
            OrgId = lease.OrgId,
            Kind = kind,
            FileName = fileName,
            StorageKey = storageKey,
            UploadedBy = uploadedBy,
            SizeBytes = content.Length,
            DocumentHash = documentHash,
            ExtractionStatus = LeaseDocumentExtractionStatus.Processing, // Set to Processing per ADR-009-R1
        };
        try
        {
            db.LeaseDocuments.Add(document);
            await db.SaveChangesAsync(ct);

            // Enqueue extraction job (ADR-009-R1)
            ExtractLeaseDocumentDataJob.EnqueueAsync(document.Id);
            logger.LogInformation("Extraction job enqueued for document {DocumentId}", document.Id);
        }
        catch
        {
            try { await storage.DeleteAsync(StorageBucket.Private, storageKey, CancellationToken.None); }
            catch (Exception cleanup) { logger.LogWarning(cleanup, "Could not remove orphan lease attachment {DocumentId}", document.Id); }
            throw;
        }
        return new LeaseDocumentDto(document.Id, lease.Id, kind, document.FileName, document.SizeBytes, document.UploadedAt);
    }

    public async Task<LeaseDocumentDownload?> DownloadAsync(Guid leaseId, Guid documentId, CancellationToken ct = default)
    {
        var document = await db.LeaseDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId && d.LeaseContractId == leaseId, ct);
        if (document is null) return null;
        var stream = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, ct);
        return stream is null ? null : new LeaseDocumentDownload(stream, document.FileName);
    }

    public async Task<LeaseReadingDto> GetReadingAsync(Guid leaseId, CancellationToken ct = default)
    {
        var documents = await db.LeaseDocuments.AsNoTracking()
            .Where(d => d.LeaseContractId == leaseId && d.Kind == LeaseDocumentKind.Contract)
            .OrderBy(d => d.UploadedAt).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.FileName, d.ExtractionStatus, d.ExtractionError, d.ExtractionResult })
            .ToListAsync(ct);

        var state = documents.Count == 0
            ? LeaseReadingStates.None
            : documents.Any(d => d.ExtractionStatus is LeaseDocumentExtractionStatus.NotStarted or LeaseDocumentExtractionStatus.Processing)
                ? LeaseReadingStates.Reading
                : documents.Any(d => d.ExtractionStatus is LeaseDocumentExtractionStatus.Completed or LeaseDocumentExtractionStatus.NeedsReview)
                    ? LeaseReadingStates.Done
                    : LeaseReadingStates.Failed;

        // One entry per field and person; a value in conflict in any document is the one to show, then one that was read.
        var fields = new Dictionary<(string Field, Guid? PartyId), LeaseReadingFieldDto>();
        string? stipulaDate = null;
        foreach (var document in documents.Where(d => d.ExtractionResult is not null))
        {
            try
            {
                using var result = JsonDocument.Parse(document.ExtractionResult!);
                stipulaDate ??= result.RootElement.TryGetProperty("stipulaDate", out var day) && day.ValueKind == JsonValueKind.String ? day.GetString() : null;
                if (!result.RootElement.TryGetProperty("fields", out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in list.EnumerateArray())
                {
                    if (!item.TryGetProperty("field", out var name) || name.ValueKind != JsonValueKind.String)
                        continue;

                    Guid? partyId = item.TryGetProperty("partyId", out var party) && party.ValueKind == JsonValueKind.String && Guid.TryParse(party.GetString(), out var id) ? id : null;
                    var entry = new LeaseReadingFieldDto(
                        name.GetString()!,
                        partyId,
                        item.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() : null,
                        item.TryGetProperty("state", out var fate) && fate.ValueKind == JsonValueKind.String ? fate.GetString()! : "Unusable",
                        item.TryGetProperty("confidence", out var confidence) && confidence.TryGetInt32(out var number) ? number : 0,
                        item.TryGetProperty("derived", out var derived) && derived.ValueKind == JsonValueKind.True);
                    var key = (entry.Field, entry.PartyId);
                    if (!fields.TryGetValue(key, out var known) || FateWeight(entry.State) > FateWeight(known.State))
                        fields[key] = entry;
                }
            }
            catch (JsonException)
            {
                // A result written by the first pipeline, or by hand: there is nothing in it for the screen.
            }
        }

        return new LeaseReadingDto(
            state,
            documents.Select(d => new LeaseReadingDocumentDto(d.Id, d.FileName, d.ExtractionStatus, d.ExtractionError)).ToList(),
            fields.Values.ToList(),
            stipulaDate,
            documents.Count > 0 ? documents[0].Id : null);
    }

    private static int FateWeight(string state) => state switch { "Conflict" => 3, "Read" => 2, "Unusable" => 1, _ => 0 };

    private static DomainRuleException InvalidPdf() => new("lease_document_invalid", "LeaseDocumentInvalid");
}
