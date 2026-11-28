using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public class ComuneOfficialProfileRefreshJob(IComuneOfficialProfileService profiles)
{
    public const string RecurringJobId = "comune-official-profile-refresh";

    /// <summary>01 of every month at 05:00 UTC: after the daily ISTAT/Alloggiati job, only on already profiled comuni.</summary>
    public const string Cron = "0 5 1 * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public Task ExecuteAsync() => profiles.RefreshAllAsync(CancellationToken.None);
}
