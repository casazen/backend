using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Monthly delta of already profiled comuni against the MEF <c>nuova_at</c> CSV (1st of the month, 05:00 UTC).
/// Does not replace <see cref="OfficialReferenceDataRefreshJob"/> or <see cref="FiscalRatesUpdateJob"/>.
/// </summary>
public class ComuneOfficialProfileRefreshJob(IComuneOfficialProfileService profiles)
{
    public const string RecurringJobId = "comune-official-profile-refresh";

    /// <summary>01 of every month at 05:00 UTC: after the daily ISTAT/Alloggiati job, only on already profiled comuni.</summary>
    public const string Cron = "0 5 1 * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public Task ExecuteAsync() => profiles.RefreshAllAsync(CancellationToken.None);
}
