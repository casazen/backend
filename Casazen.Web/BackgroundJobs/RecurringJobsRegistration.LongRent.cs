using Casazen.Core.Features;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterLongRentJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        ConfigureESignProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.ESignProvider));

        ConfigureRliProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.RliProvider));

        recurringJobManager.AddOrUpdate<PropertyModeChangeJob>(
            PropertyModeChangeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyModeChangeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<LeaseDraftRetentionJob>(
            LeaseDraftRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            LeaseDraftRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<LeaseTermRenewalJob>(
            LeaseTermRenewalJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            LeaseTermRenewalJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<LeaseNoticeReminderJob>(
            LeaseNoticeReminderJob.RecurringJobId,
            job => job.ExecuteAsync(),
            LeaseNoticeReminderJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<RliDeadlineReminderJob>(
            "rli-deadline-reminder",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    private static void ConfigureRliProviderJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(LeaseRegistrationStatusPollingJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<LeaseRegistrationStatusPollingJob>(
            LeaseRegistrationStatusPollingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    private static void ConfigureESignProviderJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(LeaseSignStatusPollingJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<LeaseSignStatusPollingJob>(
            LeaseSignStatusPollingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "*/10 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
