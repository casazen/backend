namespace Casazen.Core.Services;

public interface ILeaseDocumentExtractionService
{
    Task ExtractAsync(Guid documentId, CancellationToken ct = default);
}
