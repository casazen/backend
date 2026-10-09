using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly retention of the activity log of the orgs (AM-02b, wave decision D17): the lines older than
/// <c>OrgTeam:ActivityRetentionMonths</c> (12 months by default, to be confirmed with the legal advisor) are deleted, for every
/// org. All the decisions and the lock are in <see cref="IOrgActivityRetentionService"/>; this only runs it. Idempotent.
/// Runbooks: <c>docs/runbooks/org-team.md</c>, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class OrgActivityRetentionJob(IOrgActivityRetentionService retention)
{
    public const string RecurringJobId = "org-activity-retention";

    /// <summary>Every night at 03:40 UTC (after the SEO event retention at 03:30).</summary>
    public const string Cron = "40 3 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await retention.RunAsync();
}
