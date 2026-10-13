using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Applies an e-signature provider webhook event queued by <c>POST webhooks/esign</c> (LT-02). The signature of the body
/// was verified before queueing; the lease status guard and the flag check are in
/// <see cref="ILeaseSigningService.HandleProviderEventAsync"/>.
/// </summary>
public class ESignWebhookJob(ILeaseSigningService leaseSigningService, ILogger<ESignWebhookJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task ProcessEventAsync(string payload)
    {
        logger.LogInformation("Processing e-sign webhook event");
        try
        {
            await leaseSigningService.HandleProviderEventAsync(payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing e-sign webhook event");
            throw;
        }
    }
}
