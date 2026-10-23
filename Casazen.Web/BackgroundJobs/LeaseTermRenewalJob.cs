using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Hourly tacit renewal of the long-term leases (LR-14, decision D5 of the interview of the area): a libero (4+4) or concordato (3+2) lease
/// that is signed by every party, has no notice and was not ended early, and whose period ended before today (Europe/Rome), goes on for one
/// more period (48 months for the libero, 24 for the concordato), one period at a time, an event <c>TermRenewed</c> for each; an active rent plan
/// is extended to the new end. All the decisions, the row lock of each lease and the lock of the run are in <see cref="ILeaseTermService"/>;
/// this only runs it. Idempotent: a lease that was renewed is no longer due, so a second run, a retry or a manual trigger changes nothing, and a
/// lease that ended before the renewal was introduced was recorded as concluded by the migration and is never renewed. It writes to nobody:
/// no e-mail goes to a tenant. Always registered. Runbooks: <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10), <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class LeaseTermRenewalJob(ILeaseTermService leaseTerms)
{
    public const string RecurringJobId = "lease-term-renewal";

    /// <summary>
    /// Every hour, at minute 15 UTC: the first run after midnight of Rome (minute 0 of 22:00 or 23:00 UTC is when a lease ends) renews what
    /// ended the day before, a quarter of an hour after the other hourly jobs have started.
    /// </summary>
    public const string Cron = "15 * * * *";

    // Idempotent on its own (a lease leaves "due" in the transaction of its renewal, under its row lock), and one run at a time by a session
    // advisory lock in the service; this only keeps Hangfire from overlapping two runs.
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await leaseTerms.RenewDueAsync(cancellationToken);
}
