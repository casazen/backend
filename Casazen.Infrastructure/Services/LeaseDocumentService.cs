using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
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
        var key = StorageKeys.LeaseAttachment(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        await storage.PutAsync(StorageBucket.Private, key, content, "application/pdf", ct);
        var document = new LeaseDocument
        {
            LeaseContractId = lease.Id,
            OrgId = lease.OrgId,
            Kind = kind,
            FileName = fileName,
            StorageKey = key,
            UploadedBy = uploadedBy,
            SizeBytes = content.Length,
        };
        try
        {
            db.LeaseDocuments.Add(document);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            try { await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None); }
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

    private static DomainRuleException InvalidPdf() => new("lease_document_invalid", "LeaseDocumentInvalid");
}
