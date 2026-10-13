using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring automatic cancellation of the service requests nobody answered in time (SP-04, decision D8): the new requests past
/// their <c>ResponseDueAt</c> move to <c>Annullato</c> (reason <c>NoResponse</c>) and the host and the supplier are told
/// (<see cref="IServiceRequestAutoCancelService"/>). Scheduled only while the feature flag
/// <c>SupplierRequestAutoCancel</c> is on (off by default); the service checks the flag again, so a run that is triggered anyway
/// does nothing. Runbooks: <c>docs/runbooks/suppliers.md</c> section 21, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class ServiceRequestAutoCancelJob(IServiceRequestAutoCancelService autoCancelService, ILogger<ServiceRequestAutoCancelJob> logger)
{
    public const string RecurringJobId = "service-request-auto-cancel";

    /// <summary>Every 10 minutes: a request is cancelled at most 10 minutes after its deadline.</summary>
    public const string Cron = "*/10 * * * *";

    // One run at a time; a second run started anyway (a retry, the dashboard) waits for the first and then finds nothing due.
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await autoCancelService.CancelUnansweredAsync(cancellationToken);
        if (run.Disabled || run.Skipped)
        {
            logger.LogInformation(
                "Service request auto-cancel not run: {Reason}", run.Disabled ? "the feature flag is off" : "another run is in progress");
            return;
        }

        if (run.Failed > 0)
        {
            logger.LogWarning(
                "Service request auto-cancel: {Cancelled} cancelled, {Conflicts} changed under the run, {Failed} failed (retried by the next run)",
                run.Cancelled, run.Conflicts, run.Failed);
        }
    }
}
