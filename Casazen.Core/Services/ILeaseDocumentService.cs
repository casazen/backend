using Casazen.Core.Entities;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public sealed record LeaseDocumentDto(Guid Id, Guid LeaseId, LeaseDocumentKind Kind, string FileName, long SizeBytes, DateTime UploadedAt);
public sealed record LeaseDocumentDownload(Stream Content, string FileName);

public interface ILeaseDocumentService
{
    Task<IReadOnlyList<LeaseDocumentDto>> ListAsync(Guid leaseId, CancellationToken ct = default);
    Task<LeaseDocumentDto> UploadAsync(Guid leaseId, IFormFile file, LeaseDocumentKind kind, string uploadedBy, CancellationToken ct = default);
    Task<LeaseDocumentDownload?> DownloadAsync(Guid leaseId, Guid documentId, CancellationToken ct = default);
}
