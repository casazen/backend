using System.Text.Json;
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

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var sessionId = root.TryGetProperty("externalSessionId", out var session)
                ? session.GetString() ?? "stub"
                : "stub";
            var eventType = root.TryGetProperty("eventType", out var typeEl)
                ? typeEl.GetString() ?? "party_signed"
                : "party_signed";
            var signerEmail = root.TryGetProperty("signerEmail", out var emailEl)
                ? emailEl.GetString()
                : null;
            var allSigned = root.TryGetProperty("allSigned", out var signedEl)
                && signedEl.ValueKind == JsonValueKind.True;
            var signedPath = root.TryGetProperty("signedDocumentPath", out var pathEl)
                ? pathEl.GetString()
                : null;

            return Task.FromResult(new ESignEvent(sessionId, eventType, signerEmail, allSigned, signedPath));
        }
        catch (JsonException)
        {
            return Task.FromResult(new ESignEvent("stub", "party_signed", null, false, null));
        }
    }
}
