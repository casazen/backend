using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Stub implementation — real e-signature provider TBD.
/// Implement against chosen provider SDK when selected.
/// Config: appsettings.json → ESign:ApiKey, ESign:WebhookSecret
/// </summary>
public class LeaseESignHttpAdapter(ILogger<LeaseESignHttpAdapter> logger) : ILeaseESignService
{
    public Task<SigningSessionResult> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes)
    {
        logger.LogInformation("Initiating e-signing for LeaseId={LeaseId} Parties={Count}",
            lease.Id, lease.Parties.Count);

        // TODO: call e-sign provider API with pdfBytes
        // Return stub session ID and signing URLs for now
        var sessionId = $"stub-session-{lease.Id}";
        var signers = lease.Parties.Select(p => new SignerInfo(
            p.Id,
            p.Role,
            $"{p.FirstName} {p.LastName}",
            $"https://sign.provider.example.com/session/{sessionId}/{p.Id}",
            DateTime.UtcNow.AddDays(7)));

        return Task.FromResult(new SigningSessionResult(sessionId, signers));
    }

    public Task<ESignEvent> ParseWebhookEventAsync(string payload)
    {
        logger.LogInformation("Parsing e-sign webhook event");

        // TODO: implement real payload parsing per chosen provider
        var stub = new ESignEvent(
            ExternalSessionId: "stub",
            EventType: "party_signed",
            SignerEmail: null,
            AllSigned: false,
            SignedDocumentPath: null);

        return Task.FromResult(stub);
    }
}
