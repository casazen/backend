using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

public class LeaseSignStatusPollingJob(
    ILeaseContractRepository leaseRepository,
    ILogger<LeaseSignStatusPollingJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync()
    {
        var pendingLeases = await leaseRepository.GetByStatusAsync(LeaseStatus.AwaitingSignature);
        logger.LogInformation("Polling sign status for {Count} leases awaiting signature", pendingLeases.Count());

        foreach (var lease in pendingLeases)
        {
            // Polling is handled by the e-sign provider webhook in the normal flow.
            // TODO(#177): implement active status poll when e-sign provider is selected.
            // For now, log so operators can detect stuck leases.
            logger.LogInformation("Lease awaiting signature. LeaseId={LeaseId} Since={Since:O}",
                lease.Id, lease.UpdatedAt);
        }
    }
}
