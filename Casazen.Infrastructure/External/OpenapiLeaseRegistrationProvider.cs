using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// RLI registration via Openapi.it Docuengine. Filing stays stub until counsel sign-off (AC8).
/// </summary>
public class OpenapiLeaseRegistrationProvider(
    IOptions<RliOptions> rliOptions,
    ILogger<OpenapiLeaseRegistrationProvider> logger) : ILeaseRegistrationService
{
    public async Task<string> SubmitRegistrationAsync(LeaseContract lease)
    {
        logger.LogInformation(
            "Submitting RLI registration for LeaseId={LeaseId} FilingEnabled={FilingEnabled}",
            lease.Id, rliOptions.Value.FilingEnabled);

        var stubExternalId = $"RLI-STUB-{lease.Id:N}";
        logger.LogInformation("Registration submitted (stub). ExternalId={ExternalId}", stubExternalId);
        return await Task.FromResult(stubExternalId);
    }

    public async Task<RegistrationStatusResult> PollStatusAsync(string externalRegistrationId)
    {
        logger.LogInformation("Polling registration status for ExternalId={ExternalId}", externalRegistrationId);

        return await Task.FromResult(new RegistrationStatusResult(
            externalRegistrationId,
            "Pending",
            null,
            false));
    }

    public async Task<Stream> DownloadReceiptAsync(string externalRegistrationId)
    {
        logger.LogInformation("Downloading receipt for ExternalId={ExternalId}", externalRegistrationId);

        var placeholder = System.Text.Encoding.UTF8.GetBytes("[RECEIPT PLACEHOLDER]");
        return await Task.FromResult<Stream>(new MemoryStream(placeholder));
    }
}
