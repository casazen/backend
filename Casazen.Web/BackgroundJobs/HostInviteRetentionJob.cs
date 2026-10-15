using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily minimization of the closed host invites (GDPR): the closed ones nobody used are deleted, the used ones lose the
/// email of the invitee, <c>HostInvites:RetentionDays</c> days after they were closed. All the decisions and the lock are in
/// <see cref="IHostInviteRetentionService"/>; this only runs it.
/// </summary>
public class HostInviteRetentionJob(IHostInviteRetentionService retention)
{
    public const string RecurringJobId = "host-invite-retention";

    /// <summary>Every day at 03:40 UTC (after the GDPR retention of 03:00).</summary>
    public const string Cron = "40 3 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await retention.RunAsync();
}
