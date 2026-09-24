using System.Collections.Concurrent;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Tests.Integration.Fakes;

/// <summary>
/// Test-only RLI filing provider (LT-01). Every call is counted per lease, so a test can prove the provider was never
/// reached; a submission can be made to fail once, and the status of each request is set by the test (default: in
/// progress). The receipt is a small PDF. The production registration is <c>UnconfiguredLeaseRegistrationProvider</c>.
/// </summary>
public sealed class FakeLeaseRegistrationProvider : ILeaseRegistrationProvider
{
    private readonly ConcurrentDictionary<Guid, int> _calls = new();
    private readonly ConcurrentDictionary<Guid, byte> _failNextSubmission = new();
    private readonly ConcurrentDictionary<Guid, ProviderRegistrationStatus> _statuses = new();

    public static readonly byte[] ReceiptPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% provider receipt\n%%EOF\n");

    public bool IsConfigured { get; set; } = true;

    /// <summary>Submit, status and receipt calls made for <paramref name="leaseId"/>.</summary>
    public int CallsFor(Guid leaseId) => _calls.GetValueOrDefault(leaseId);

    public void FailNextSubmission(Guid leaseId) => _failNextSubmission[leaseId] = 0;

    public void SetStatus(Guid leaseId, ProviderRegistrationStatus status) => _statuses[leaseId] = status;

    public static string ExternalIdFor(Guid leaseId) => $"PROVIDER-{leaseId:N}";

    public Task<string> SubmitAsync(LeaseContract lease, CancellationToken cancellationToken = default)
    {
        Count(lease.Id);
        if (_failNextSubmission.TryRemove(lease.Id, out _))
            throw new LeaseRegistrationProviderException(RliRegistrationFailureCodes.ProviderError, "Simulated provider outage.");
        return Task.FromResult(ExternalIdFor(lease.Id));
    }

    public Task<ProviderRegistrationStatus> GetStatusAsync(string externalRegistrationId, CancellationToken cancellationToken = default)
    {
        var leaseId = LeaseIdOf(externalRegistrationId);
        Count(leaseId);
        return Task.FromResult(
            _statuses.GetValueOrDefault(leaseId) ?? new ProviderRegistrationStatus(ProviderRegistrationState.InProgress));
    }

    public Task<Stream> DownloadReceiptAsync(string externalRegistrationId, CancellationToken cancellationToken = default)
    {
        Count(LeaseIdOf(externalRegistrationId));
        return Task.FromResult<Stream>(new MemoryStream(ReceiptPdf));
    }

    private void Count(Guid leaseId) => _calls.AddOrUpdate(leaseId, 1, (_, calls) => calls + 1);

    private static Guid LeaseIdOf(string externalRegistrationId) =>
        Guid.ParseExact(externalRegistrationId["PROVIDER-".Length..], "N");
}
