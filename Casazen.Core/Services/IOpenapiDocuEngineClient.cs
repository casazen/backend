using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.Services;

/// <summary>
/// Openapi DocuEngine client for the lease-contract registration product
/// (<c>POST /requests</c>, <c>GET /requests/{id}</c>, <c>GET /requests/{id}/documents</c>).
/// One product covers all residential contract types supported by CasaZen.
/// </summary>
public interface IOpenapiDocuEngineClient
{
    /// <summary>True when <c>Openapi</c> has an origin, a service id and credentials.</summary>
    bool IsConfigured { get; }

    /// <summary>Opens the filing. Returns the DocuEngine request id. Throws <see cref="LeaseRegistrationProviderException"/> on failure.</summary>
    Task<string> SubmitLeaseRegistrationAsync(
        LeaseContract lease,
        ReadOnlyMemory<byte> signedContract,
        CancellationToken cancellationToken = default);

    Task<ProviderRegistrationStatus> GetStatusAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>The official receipt PDF. The caller disposes the stream.</summary>
    Task<Stream> DownloadReceiptAsync(string requestId, CancellationToken cancellationToken = default);
}
