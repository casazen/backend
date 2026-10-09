using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Hourly work on the invitations to an org (AM-02): the reminder of the third day (with a fresh link), the expiry of the
/// invitations nobody accepted (the inviter gets a note), and the deletion of the closed ones, with the name and the email
/// of the invitee, once the retention has passed. All the decisions and the locks are in
/// <see cref="IOrgInvitationMaintenanceService"/>; this only runs it. Runbooks: <c>docs/runbooks/org-team.md</c>,
/// <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class OrgInvitationMaintenanceJob(IOrgInvitationMaintenanceService maintenance)
{
    public const string RecurringJobId = "org-invitation-maintenance";

    /// <summary>Every hour at minute 10 (the other hourly jobs run on the hour).</summary>
    public const string Cron = "10 * * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await maintenance.RunAsync();
}
