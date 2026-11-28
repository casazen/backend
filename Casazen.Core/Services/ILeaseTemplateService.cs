using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ILeaseTemplateService
{
    Task<byte[]> GeneratePdfAsync(LeaseContract lease);

    void EnsureFinalContractAvailable(LeaseContract lease);

    string? GetFinalContractBlocker(LeaseContract lease);

    Task<byte[]> GeneratePreviewPdfAsync(LeaseContract lease);
}
