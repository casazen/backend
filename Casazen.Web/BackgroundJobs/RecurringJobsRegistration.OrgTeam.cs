using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The people of an org: the maintenance of the invitations, the retention of the host invites and of the activity log.
    /// </summary>
    private static void RegisterOrgTeamJobs(IRecurringJobManager recurringJobManager)
    {
        // AM-02: reminder of the third day, expiry and deletion of the closed org invitations.
        recurringJobManager.AddOrUpdate<OrgInvitationMaintenanceJob>(
            OrgInvitationMaintenanceJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgInvitationMaintenanceJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Host invites: the email of a closed invite is erased after HostInvites:RetentionDays.
        recurringJobManager.AddOrUpdate<HostInviteRetentionJob>(
            HostInviteRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            HostInviteRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // AM-02b: the activity log keeps 12 months.
        recurringJobManager.AddOrUpdate<OrgActivityRetentionJob>(
            OrgActivityRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgActivityRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
