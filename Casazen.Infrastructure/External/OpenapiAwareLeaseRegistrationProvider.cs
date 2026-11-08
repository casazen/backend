using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Keeps the existing provider for every submit (concordato included) and, when that provider is not configured,
/// reads status and receipts from DocuEngine so a non-concordato filing can finish. Submits of non-concordato
/// contracts go through <see cref="IOpenapiDocuEngineClient"/> from <see cref="RliRegistrationService"/>, not from here.
/// </summary>
public sealed class OpenapiAwareLeaseRegistrationProvider(
    ILeaseRegistrationProvider inner,
    IOpenapiDocuEngineClient openapi) : ILeaseRegistrationProvider
{
    public bool IsConfigured => inner.IsConfigured;

    public Task<string> SubmitAsync(LeaseContract lease, CancellationToken cancellationToken = default) =>
        inner.SubmitAsync(lease, cancellationToken);

    public Task<ProviderRegistrationStatus> GetStatusAsync(string externalRegistrationId, CancellationToken cancellationToken = default) =>
        UseOpenapi ? openapi.GetStatusAsync(externalRegistrationId, cancellationToken) : inner.GetStatusAsync(externalRegistrationId, cancellationToken);

    public Task<Stream> DownloadReceiptAsync(string externalRegistrationId, CancellationToken cancellationToken = default) =>
        UseOpenapi ? openapi.DownloadReceiptAsync(externalRegistrationId, cancellationToken) : inner.DownloadReceiptAsync(externalRegistrationId, cancellationToken);

    private bool UseOpenapi => !inner.IsConfigured && openapi.IsConfigured;
}
