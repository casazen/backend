using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily deletion of the abandoned lease drafts (LR-12, decision LR-D33): a draft nobody changed for
/// <c>LongRentLease:DraftRetentionDays</c> days (180 by default) is deleted with its parties, events and files; one with a signed
/// copy never is. All the decisions and the lock are in <see cref="ILeaseDraftRetentionService"/>; this only runs it, and it logs only
/// a count. Idempotent. Registered only with <c>Features:LongRentArea</c> on. Runbooks: <c>docs/runbooks/long-rent-lease-draft.md</c>,
/// <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class LeaseDraftRetentionJob(ILeaseDraftRetentionService retention)
{
    public const string RecurringJobId = "lease-draft-retention";

    /// <summary>Every day at 03:50 UTC (after the GDPR retention of 03:00 and the other retention jobs of 03:30 and 03:40).</summary>
    public const string Cron = "50 3 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await retention.RunAsync();
}
