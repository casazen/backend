using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Tests.Integration.Fakes;

/// <summary>
/// Test-only RLI provider: submit is a stub id, poll confirms, receipt is a non-empty PDF.
/// Production <c>OpenapiLeaseRegistrationProvider</c> never confirms (live HTTP is frozen).
/// </summary>
public sealed class ConfirmingLeaseRegistrationService : ILeaseRegistrationService
{
    public const string RegistrationCode = "RLI-CODE-TEST-1";

    public Task<string> SubmitRegistrationAsync(LeaseContract lease)
        => Task.FromResult($"RLI-TEST-{lease.Id:N}");

    public Task<RegistrationStatusResult> PollStatusAsync(string externalRegistrationId)
        => Task.FromResult(new RegistrationStatusResult(
            externalRegistrationId,
            "Registered",
            RegistrationCode,
            true));

    public Task<Stream> DownloadReceiptAsync(string externalRegistrationId)
    {
        var bytes = Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n% receipt {externalRegistrationId}\n%%EOF\n");
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
