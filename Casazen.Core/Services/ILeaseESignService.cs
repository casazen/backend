using Casazen.Core.Entities;
using Casazen.Core.Features;

namespace Casazen.Core.Services;

/// <summary>
/// External e-signature provider (LT-02, A7-02, D15). The candidate for phase 2 is Yousign/Youtrust API v3 with AES
/// (docs/integrations/rli-esign.md §3), but no client is written yet: a legally valid lease signature needs at least
/// the FEA, which no provider offers self-serve, and the budget and the legal opinion on the FEA are open. The default
/// registration is an unconfigured provider. The provider is used only when <see cref="ESignProviderSigning.IsAvailable"/>:
/// <c>Features:ESignProvider</c> on <b>and</b> <see cref="IsConfigured"/>; otherwise the contract is signed offline.
/// </summary>
/// <remarks>
/// A provider never reports a signature it has not received: <see cref="InitiateSigningAsync"/> returns the provider's
/// session and the personal links, and only an all-signed webhook event together with the downloadable signed PDF makes
/// the lease Signed.
/// </remarks>
public interface ILeaseESignService
{
    bool IsConfigured { get; }

    Task<SigningSessionResult> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes, CancellationToken cancellationToken = default);

    Task<ESignEvent?> ParseWebhookEventAsync(string payload);

    Task<Stream> DownloadSignedDocumentAsync(string externalSessionId, CancellationToken cancellationToken = default);
}

public sealed record SigningSessionResult(string ExternalSessionId, IReadOnlyList<ProviderSigner> Signers);

public sealed record ProviderSigner(Guid PartyId, string ExternalSignerId, string SigningUrl, DateTime ExpiresAt);

public enum ESignEventKind
{
    SignerSigned,

    AllSigned,

    Other,
}

/// <summary>
/// A provider webhook event. <paramref name="ExternalSignerId"/> identifies the signer of a
/// <see cref="ESignEventKind.SignerSigned"/> event (never an email: event handling stores no personal data).
/// </summary>
public sealed record ESignEvent(string ExternalSessionId, ESignEventKind Kind, string? ExternalSignerId = null);

public sealed class ESignProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>When the provider signature path exists (LT-02): the flag on and a configured provider. Otherwise offline only.</summary>
public static class ESignProviderSigning
{
    public static bool IsAvailable(IFeatureFlags featureFlags, ILeaseESignService provider)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(provider);
        return featureFlags.IsEnabled(FeatureFlags.ESignProvider) && provider.IsConfigured;
    }
}
