using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

public class ESignWebhookJob(ILeaseWorkflowService leaseWorkflowService, ILogger<ESignWebhookJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task ProcessEventAsync(string payload)
    {
        logger.LogInformation("Processing e-sign webhook event");
        try
        {
            await leaseWorkflowService.HandleESignEventAsync(payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing e-sign webhook event");
            throw;
        }
    }
}
