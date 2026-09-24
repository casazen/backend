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
            "dynamic-pricing-adaptation",
            job => job.ExecuteAsync(),
            "0 2 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GdprDataRetentionJob>(
            "gdpr-data-retention",
            job => job.ExecuteAsync(),
            "0 3 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<AlloggiatiDeadlineAlertJob>(
            "alloggiati-deadline-alert",
            job => job.ExecuteAsync(),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CinDeadlineAlertJob>(
            "cin-deadline-alert",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<LeaseSignStatusPollingJob>(
            "lease-sign-status-poll",
            job => job.ExecuteAsync(),
            "*/10 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<LeaseRegistrationStatusPollingJob>(
            "lease-registration-status-poll",
            job => job.ExecuteAsync(),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

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

        recurringJobManager.AddOrUpdate<GuestCheckInReminderJob>(
            "guest-checkin-reminder",
            job => job.ExecuteAsync(),
            "0 10 * * *",
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
