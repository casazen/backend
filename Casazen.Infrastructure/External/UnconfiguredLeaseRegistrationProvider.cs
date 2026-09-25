using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Default <see cref="ILeaseRegistrationProvider"/> (LT-01, A7-01): there is no RLI filing provider client yet, so the
/// provider path does not exist even with <c>Features:RliProvider</c> on and every lease is registered manually.
/// It replaces the old Openapi stub, which returned <c>RLI-STUB-{id}</c>, never confirmed and served
/// <c>[RECEIPT PLACEHOLDER]</c> as a PDF.
/// </summary>
/// <remarks>
/// Why no real Openapi client: the id and the input fields of the DocuEngine "lease registration" service are not in
/// the public specification and can only be read with an account (<c>GET /documents</c>), see
/// docs/integrations/rli-esign.md §1.7 and docs/runbooks/rli.md. Writing the mapping without them would invent the
/// payload. Every method refuses to run, so nothing can ever report a filing that did not happen.
/// </remarks>
public sealed class UnconfiguredLeaseRegistrationProvider : ILeaseRegistrationProvider
{
    public bool IsConfigured => false;

    public Task<string> SubmitAsync(LeaseContract lease, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<ProviderRegistrationStatus> GetStatusAsync(string externalRegistrationId, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<Stream> DownloadReceiptAsync(string externalRegistrationId, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    private static LeaseRegistrationProviderException NotConfigured() =>
        new(RliRegistrationFailureCodes.ProviderError, "No RLI filing provider is configured.");
}
