using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily check of the official ISTAT comuni file, the Alloggiati Web code tables and the tourist-tax pages of the
/// configured pilot comuni (RS-6, RS-7, CO-12). Downloads from institutional URLs only; never invents amounts.
/// </summary>
public class OfficialReferenceDataRefreshJob(IOfficialReferenceDataRefreshService refresh)
{
    public const string RecurringJobId = "official-reference-data-refresh";

    /// <summary>04:30 UTC every day: ISTAT publishes after territorial changes, Alloggiati tables are small.</summary>
    public const string Cron = "30 4 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public Task ExecuteAsync() => refresh.RefreshAsync(CancellationToken.None);
}
