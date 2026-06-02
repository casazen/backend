using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ILeaseESignService
{
    Task<IEnumerable<SignerInfo>> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes);
    Task<ESignEvent> ParseWebhookEventAsync(string payload);
}

public record ESignEvent(
    string ExternalSessionId,
    string EventType,
    string? SignerEmail,
    bool AllSigned,
    string? SignedDocumentPath);
