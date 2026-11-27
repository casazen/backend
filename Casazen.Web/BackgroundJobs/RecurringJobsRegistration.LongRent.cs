using Casazen.Core.Features;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The long-term area: the e-signature and RLI provider polling, the scheduled change of rental mode, the lease drafts, renewals and notices, and
    /// the RLI deadlines.
    /// </summary>
    private static void RegisterLongRentJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        ConfigureESignProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.ESignProvider));

        ConfigureRliProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.RliProvider));

        // PM-02: application of the scheduled changes of rental mode, every hour.
        recurringJobManager.AddOrUpdate<PropertyModeChangeJob>(
            PropertyModeChangeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyModeChangeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LR-12: the abandoned lease drafts (no change for LongRentLease:DraftRetentionDays) are deleted every night.
        recurringJobManager.AddOrUpdate<LeaseDraftRetentionJob>(
            LeaseDraftRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            LeaseDraftRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LR-14: the leases whose period is over and that nobody gave notice of renew by themselves, every hour.
        recurringJobManager.AddOrUpdate<LeaseTermRenewalJob>(
            LeaseTermRenewalJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            LeaseTermRenewalJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // LR-11c-a: the landlord is reminded of the last day to give notice of a lease (30 and 7 days before, and that day), every day.
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

    /// <summary>
    /// RLI provider polling (LT-01): only with <see cref="FeatureFlags.RliProvider"/> on. With the flag off every lease is
    /// registered manually and there is nothing to poll; the schedule of an earlier deploy is removed.
    /// </summary>
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

    /// <summary>
    /// E-signature provider watch (LT-02): only with <see cref="FeatureFlags.ESignProvider"/> on. With the flag off every
    /// contract is signed offline and no lease waits for a provider; the schedule of an earlier deploy is removed.
    /// </summary>
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
