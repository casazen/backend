using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Default <see cref="ILeaseESignService"/> (LT-02, A7-02): there is no e-signature provider client yet, so the provider
/// path does not exist even with <c>Features:ESignProvider</c> on and every contract is signed offline. It replaces the
/// old stub <c>LeaseESignHttpAdapter</c>, which returned links to <c>sign.provider.example.com</c> and moved the lease to
/// AwaitingSignature forever.
/// </summary>
/// <remarks>
/// Why no real client: a lease signed electronically needs at least the FEA (CAD art. 20), which no provider offers
/// self-serve; the candidate (Yousign/Youtrust API v3, AES) needs an annual plan and a legal opinion on the FEA
/// obligations (DPCM 22/02/2013 art. 57), see docs/integrations/rli-esign.md §3 and docs/runbooks/rli.md. Every method
/// refuses to run, so nothing can ever report a signature that did not happen.
/// </remarks>
public sealed class UnconfiguredLeaseESignService : ILeaseESignService
{
    public bool IsConfigured => false;

    public Task<SigningSessionResult> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<ESignEvent?> ParseWebhookEventAsync(string payload) =>
        throw NotConfigured();

    public Task<Stream> DownloadSignedDocumentAsync(string externalSessionId, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    private static ESignProviderException NotConfigured() => new("No e-signature provider is configured.");
}
