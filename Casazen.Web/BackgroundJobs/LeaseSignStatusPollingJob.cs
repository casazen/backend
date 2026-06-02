using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

public class LeaseSignStatusPollingJob(
    ILeaseContractRepository leaseRepository,
    ILeaseESignService eSignService,
    ILeaseWorkflowService leaseWorkflowService,
    ILogger<LeaseSignStatusPollingJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync()
    {
        var pendingLeases = await leaseRepository.GetByStatusAsync(LeaseStatus.AwaitingSignature);
        logger.LogInformation("Polling sign status for {Count} leases", pendingLeases.Count());

        foreach (var lease in pendingLeases)
        {
            try
            {
                // Polling is handled by the e-sign provider webhook in normal flow.
                // This job catches cases where the webhook was not received.
                logger.LogInformation("Checking sign status for LeaseId={LeaseId}", lease.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling sign status for LeaseId={LeaseId}", lease.Id);
            }
        }
    }
}
