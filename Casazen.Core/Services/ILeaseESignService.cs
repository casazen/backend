using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ILeaseESignService
{
    Task<SigningSessionResult> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes);
    Task<ESignEvent> ParseWebhookEventAsync(string payload);
}

public record SigningSessionResult(string ExternalSessionId, IEnumerable<SignerInfo> Signers);

public record ESignEvent(
    string ExternalSessionId,
    string EventType,
    string? SignerEmail,
    bool AllSigned,
    string? SignedDocumentPath);
