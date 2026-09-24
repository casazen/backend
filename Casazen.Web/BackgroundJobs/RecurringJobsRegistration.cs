using Casazen.Core.Features;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring jobs, registered (<c>AddOrUpdate</c>) on every startup in the environment's own Hangfire schema.
/// Every job listed here must carry <see cref="DisableConcurrentExecutionAttribute"/> (FD-11): a run that is
/// still in progress, a retry or a manual trigger from the dashboard must never overlap with the next run.
/// A job behind a feature flag that is off is removed (<c>RemoveIfExists</c>), so an earlier deploy's schedule stops.
/// </summary>
public static class RecurringJobsRegistration
{
    public const string OtaSyncAllJobId = "ota-sync-all";
    public const string BookingPullAllJobId = "booking-pull-all";

    public static void Configure(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        ConfigureOtaPartnerJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OtaPartnerApi));

        recurringJobManager.AddOrUpdate<DynamicPricingJob>(
            DynamicPricingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 2 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GdprDataRetentionJob>(
            "gdpr-data-retention",
            job => job.ExecuteAsync(),
            "0 3 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-10: one hourly job for the stay alerts; the two jobs it replaces are removed from the schedule.
        foreach (var replaced in StayAlertsJob.ReplacedRecurringJobIds)
            recurringJobManager.RemoveIfExists(replaced);

        recurringJobManager.AddOrUpdate<StayAlertsJob>(
            StayAlertsJob.RecurringJobId,
            job => job.ExecuteAsync(),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CinDeadlineAlertJob>(
            "cin-deadline-alert",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureESignProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.ESignProvider));

        ConfigureRliProviderJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.RliProvider));

        recurringJobManager.AddOrUpdate<RliDeadlineReminderJob>(
            "rli-deadline-reminder",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<SeoContentRefreshJob>(
            "seo-content-refresh",
            job => job.ExecuteAsync(),
            "0 4 1 * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<DirectBookingChargeJob>(
            "direct-booking-charge",
            job => job.ExecuteAsync(),
            "0 6 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CheckoutHoldExpiryJob>(
            CheckoutHoldExpiryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CheckoutHoldExpiryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<IcalSupplierSyncJob>(
            "ical-supplier-sync",
            job => job.ExecuteAsync(),
            "*/15 * * * *",  // Every 15 minutes
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<PropertyICalSyncJob>(
            "property-ical-sync",
            job => job.ExecuteAsync(),
            "*/15 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GuestCheckInSendJob>(
            "guest-checkin-send",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-06: nightly compliance check of every published property (suspends the ones that lost a requirement).
        recurringJobManager.AddOrUpdate<PropertyComplianceCheckJob>(
            PropertyComplianceCheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyComplianceCheckJob.Cron,
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

    /// <summary>
    /// OTA partner API jobs (D10, in freeze): only with <see cref="FeatureFlags.OtaPartnerApi"/> on. They still run with
    /// <see cref="Guid.Empty"/> (no property: a no-op with a warning, A9-15), to be rewritten before the flag is turned on.
    /// </summary>
    private static void ConfigureOtaPartnerJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OtaSyncAllJobId);
            recurringJobManager.RemoveIfExists(BookingPullAllJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OtaSyncJob>(
            OtaSyncAllJobId,
            job => job.ExecuteAsync(Guid.Empty),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<BookingPullJob>(
            BookingPullAllJobId,
            job => job.ExecuteAsync(Guid.Empty),
            "*/15 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
