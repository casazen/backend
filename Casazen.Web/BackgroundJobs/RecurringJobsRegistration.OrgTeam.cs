using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterOrgTeamJobs(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<OrgInvitationMaintenanceJob>(
            OrgInvitationMaintenanceJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgInvitationMaintenanceJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<HostInviteRetentionJob>(
            HostInviteRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            HostInviteRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OrgActivityRetentionJob>(
            OrgActivityRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OrgActivityRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
