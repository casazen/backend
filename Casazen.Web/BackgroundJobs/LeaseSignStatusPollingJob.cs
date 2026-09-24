using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Watch on the leases waiting for the e-signature provider (LT-02), registered only with
/// <c>Features:ESignProvider</c> on. The provider reports signatures through the webhook; this job only logs the leases
/// still waiting so operators can spot a stuck session. It never changes a lease and never calls the provider (no
/// status API is integrated).
/// </summary>
public class LeaseSignStatusPollingJob(
    ILeaseContractRepository leaseRepository,
    ILogger<LeaseSignStatusPollingJob> logger)
{
    public const string RecurringJobId = "lease-sign-status-poll";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync()
    {
        var waiting = (await leaseRepository.GetByStatusAsync(LeaseStatus.AwaitingSignature))
            .Concat(await leaseRepository.GetByStatusAsync(LeaseStatus.PartiallySigned))
            .ToList();
        logger.LogInformation("{Count} leases waiting for the e-signature provider", waiting.Count);

        foreach (var lease in waiting)
        {
            logger.LogInformation("Lease waiting for the e-signature provider. LeaseId={LeaseId} Since={Since:O}",
                lease.Id, lease.UpdatedAt);
        }
    }
}
