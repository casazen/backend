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
    /// <summary>True only when every setting the provider needs (endpoint, API key, webhook secret) is present.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Opens a signing session for the final contract <paramref name="pdfBytes"/> with one signer per party. Throws
    /// <see cref="ESignProviderException"/> when the provider does not take the request.
    /// </summary>
    Task<SigningSessionResult> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a webhook body whose signature was already verified. Null when the body is not an event of this provider.
    /// </summary>
    Task<ESignEvent?> ParseWebhookEventAsync(string payload);

    /// <summary>The PDF signed by every party of a completed session. The caller disposes the stream.</summary>
    Task<Stream> DownloadSignedDocumentAsync(string externalSessionId, CancellationToken cancellationToken = default);
}

/// <summary>A signing session opened by the provider.</summary>
public sealed record SigningSessionResult(string ExternalSessionId, IReadOnlyList<ProviderSigner> Signers);

/// <summary>The personal signing link of one party. <paramref name="ExpiresAt"/> is a UTC instant.</summary>
public sealed record ProviderSigner(Guid PartyId, string ExternalSignerId, string SigningUrl, DateTime ExpiresAt);

/// <summary>Kind of a provider event.</summary>
public enum ESignEventKind
{
    /// <summary>One signer signed; others may still be pending.</summary>
    SignerSigned,

    /// <summary>Every signer signed: the signed document can be downloaded.</summary>
    AllSigned,

    /// <summary>Any other event (viewed, reminder, ...): recorded nowhere.</summary>
    Other,
}

/// <summary>
/// A provider webhook event. <paramref name="ExternalSignerId"/> identifies the signer of a
/// <see cref="ESignEventKind.SignerSigned"/> event (never an email: event handling stores no personal data).
/// </summary>
public sealed record ESignEvent(string ExternalSessionId, ESignEventKind Kind, string? ExternalSignerId = null);

/// <summary>The e-signature provider could not take or process a request (network, credit, validation, outage).</summary>
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
