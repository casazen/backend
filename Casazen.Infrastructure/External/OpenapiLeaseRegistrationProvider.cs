using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// RLI registration via Openapi.it Docuengine.
/// Config: appsettings.json → Openapi:ApiKey, Openapi:BaseUrl
/// Docs: https://openapi.it/prodotti/servizi-contratti-di-locazione
/// </summary>
public class OpenapiLeaseRegistrationProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OpenapiLeaseRegistrationProvider> logger) : ILeaseRegistrationService
{
    public async Task<string> SubmitRegistrationAsync(LeaseContract lease)
    {
        logger.LogInformation("Submitting RLI registration for LeaseId={LeaseId}", lease.Id);

        // TODO: POST to Openapi.it Docuengine registration endpoint
        // var apiKey = configuration["Openapi:ApiKey"];
        // var client = httpClientFactory.CreateClient("Openapi");
        // var response = await client.PostAsJsonAsync("/locazioni/registrazione", payload);

        var stubExternalId = $"RLI-STUB-{lease.Id:N}";
        logger.LogInformation("Registration submitted (stub). ExternalId={ExternalId}", stubExternalId);
        return await Task.FromResult(stubExternalId);
    }

    public async Task<RegistrationStatusResult> PollStatusAsync(string externalRegistrationId)
    {
        logger.LogInformation("Polling registration status for ExternalId={ExternalId}", externalRegistrationId);

        // TODO: GET Openapi.it status endpoint
        return await Task.FromResult(new RegistrationStatusResult(
            externalRegistrationId,
            "Pending",
            null,
            false));
    }

    public async Task<Stream> DownloadReceiptAsync(string externalRegistrationId)
    {
        logger.LogInformation("Downloading receipt for ExternalId={ExternalId}", externalRegistrationId);

        // TODO: GET Openapi.it receipt download endpoint
        var placeholder = System.Text.Encoding.UTF8.GetBytes("[RECEIPT PLACEHOLDER]");
        return await Task.FromResult<Stream>(new MemoryStream(placeholder));
    }
}
