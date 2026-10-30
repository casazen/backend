using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring pass over the groups of backup suppliers (SP-BKP): when the primary supplier's hour is over, the job goes to the earliest backup
/// that accepted; when nobody had accepted, the second window opens, and when it is over the requests nobody took lapse with
/// <c>NoResponse</c> (<see cref="IServiceRequestGroupRunService"/>). <b>Registered whatever the feature flags say</b>: the flag
/// <c>SupplierRequestBackups</c> only gates the creation of groups, and a group that exists has to be followed to the end even if the flag
/// is turned off afterwards; with no group a run finds nothing. It does not duplicate <see cref="ServiceRequestAutoCancelJob"/>, which leaves
/// the requests of a group alone. Runbooks: <c>docs/runbooks/suppliers.md</c> section 36, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class ServiceRequestGroupJob(IServiceRequestGroupRunService runService, ILogger<ServiceRequestGroupJob> logger)
{
    public const string RecurringJobId = "service-request-groups";

    /// <summary>Every 5 minutes: a window is followed at most 5 minutes after it ends.</summary>
    public const string Cron = "*/5 * * * *";

    // One run at a time; a second run started anyway (a retry, the dashboard) waits for the first and then finds nothing due.
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await runService.RunAsync(cancellationToken);
        if (run.Skipped)
        {
            logger.LogInformation("Service request groups not run: another run is in progress");
            return;
        }

        if (run.Failed > 0)
        {
            logger.LogWarning(
                "Service request groups: {Examined} examined, {Closed} closed, {Windows} second windows opened, {Conflicts} changed under the run, {Failed} failed (retried by the next run)",
                run.Examined, run.Closed, run.BackupWindowsOpened, run.Conflicts, run.Failed);
        }
    }
}
