using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Tests.Integration.Fakes;

/// <summary>
/// Test-only e-signature provider (LT-02). Every call is counted, so a test can prove the provider was never reached.
/// Sessions are <c>fake-session-{leaseId:N}</c>, signers <c>fake-signer-{partyId:N}</c>, links on the reserved
/// <c>.invalid</c> domain. Webhook bodies are the JSON of <see cref="EventBody"/>. The production registration is
/// <c>UnconfiguredLeaseESignService</c>.
/// </summary>
public sealed class FakeLeaseESignProvider : ILeaseESignService
{
    private int _calls;

    public static readonly byte[] SignedPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% contratto firmato dal provider di test\n%%EOF\n");

    public bool IsConfigured { get; set; } = true;

    /// <summary>Every call to the provider (initiate, parse, download).</summary>
    public int Calls => Volatile.Read(ref _calls);

    public static string SessionIdFor(Guid leaseId) => $"fake-session-{leaseId:N}";

    public static string SignerIdFor(Guid partyId) => $"fake-signer-{partyId:N}";

    /// <summary>Webhook body understood by <see cref="ParseWebhookEventAsync"/>: <c>kind</c> is signer_signed or all_signed.</summary>
    public static string EventBody(Guid leaseId, string kind, Guid? partyId = null) =>
        JsonSerializer.Serialize(new
        {
            sessionId = SessionIdFor(leaseId),
            kind,
            signerId = partyId is { } id ? SignerIdFor(id) : null,
        });

    public Task<SigningSessionResult> InitiateSigningAsync(LeaseContract lease, byte[] pdfBytes, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var signers = lease.Parties
            .Select(p => new ProviderSigner(
                p.Id,
                SignerIdFor(p.Id),
                $"https://esign.invalid/sign/{SignerIdFor(p.Id)}",
                DateTime.UtcNow.AddDays(7)))
            .ToList();
        return Task.FromResult(new SigningSessionResult(SessionIdFor(lease.Id), signers));
    }

    public Task<ESignEvent?> ParseWebhookEventAsync(string payload)
    {
        Interlocked.Increment(ref _calls);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var kind = root.GetProperty("kind").GetString() switch
        {
            "signer_signed" => ESignEventKind.SignerSigned,
            "all_signed" => ESignEventKind.AllSigned,
            _ => ESignEventKind.Other,
        };
        var signerId = root.TryGetProperty("signerId", out var signer) ? signer.GetString() : null;
        return Task.FromResult<ESignEvent?>(new ESignEvent(root.GetProperty("sessionId").GetString()!, kind, signerId));
    }

    public Task<Stream> DownloadSignedDocumentAsync(string externalSessionId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult<Stream>(new MemoryStream(SignedPdf));
    }
}
