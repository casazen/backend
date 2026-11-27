using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.Services;

public interface IOpenapiDocuEngineClient
{
    bool IsConfigured { get; }

    Task<string> SubmitLeaseRegistrationAsync(
        LeaseContract lease,
        ReadOnlyMemory<byte> signedContract,
        CancellationToken cancellationToken = default);

    Task<ProviderRegistrationStatus> GetStatusAsync(string requestId, CancellationToken cancellationToken = default);

    Task<Stream> DownloadReceiptAsync(string requestId, CancellationToken cancellationToken = default);
}
