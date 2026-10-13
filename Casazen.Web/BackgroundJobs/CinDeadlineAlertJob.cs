using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily alert to the hosts of properties without a valid CIN (CO-20, A5-31), each stage at most once per property
/// (<see cref="ICinDeadlineAlertService"/>). Runs after the nightly compliance check (<see cref="PropertyComplianceCheckJob"/>,
/// 04:00), so a published property that lost its CIN is already suspended and its host has the CO-06 email only.
/// Runbooks: <c>docs/runbooks/cin-format.md</c>, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class CinDeadlineAlertJob(ICinDeadlineAlertService cinDeadlineAlertService)
{
    public const string RecurringJobId = "cin-deadline-alert";

    /// <summary>08:00 UTC (09:00 or 10:00 in Italy).</summary>
    public const string Cron = "0 8 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await cinDeadlineAlertService.RunAsync();
}
