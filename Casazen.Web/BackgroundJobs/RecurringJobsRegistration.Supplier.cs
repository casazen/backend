using Casazen.Core.Features;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterSupplierJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        recurringJobManager.AddOrUpdate<GuestSupplierAdjustmentJob>(
            "guest-supplier-stripe-adjustments", job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GuestSupplierPayoutJob>(
            "guest-supplier-sandbox-transfers", job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GuestSupplierStripeExpiryJob>(
            "guest-supplier-stripe-expiry", job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureServiceRequestAutoCancel(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.SupplierRequestAutoCancel));

        ConfigureMarketplacePortalRefresh(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        ConfigureMarketplaceConnectionSync(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        recurringJobManager.AddOrUpdate<ServiceRequestGroupJob>(
            ServiceRequestGroupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestGroupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ServiceRequestExpiryJob>(
            ServiceRequestExpiryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestExpiryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ServiceRequestReminderJob>(
            ServiceRequestReminderJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureServicePayments(recurringJobManager);

        recurringJobManager.AddOrUpdate<SupplierShowcaseMediaCleanupJob>(
            SupplierShowcaseMediaCleanupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            SupplierShowcaseMediaCleanupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    private static void ConfigureServiceRequestAutoCancel(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(ServiceRequestAutoCancelJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<ServiceRequestAutoCancelJob>(
            ServiceRequestAutoCancelJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestAutoCancelJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The daily refresh of the portal catalog cached for the marketplace (E09/F01/US01/T02): only with
    /// <see cref="FeatureFlags.OctorateConnector"/> on. With the flag off nothing calls Octorate, and the schedule of an earlier deploy is
    /// removed; the cache keeps what it had, and the marketplace goes on reading it.
    /// </summary>
    private static void ConfigureMarketplacePortalRefresh(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(MarketplacePortalRefreshJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<MarketplacePortalRefreshJob>(
            MarketplacePortalRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            MarketplacePortalRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The sync of the connections of the active integrations (E09/F03/US01/T01, NF-57): every 30 minutes, only with <see cref="FeatureFlags.OctorateConnector"/> on. It reads
    /// Octorate (the connections, the license, the quota) and writes only CasaZen's own rows and the «To do» items. With the flag off nothing calls Octorate, and the schedule of an
    /// earlier deploy is removed.
    /// </summary>
    private static void ConfigureMarketplaceConnectionSync(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(MarketplaceConnectionSyncJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<MarketplaceConnectionSyncJob>(
            MarketplaceConnectionSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            MarketplaceConnectionSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The jobs of the supplier service payments (SP-15b), <b>always</b> registered, whatever the state of
    /// <see cref="FeatureFlags.SupplierOnlinePayments"/>: payments that exist (money in flight) must be followed to the end. The sync
    /// reads the payments Stripe is processing and the refunds it has not completed; the reminders flag the late payments and, with
    /// the flag on, send the pending requests and the reminders. The flag is read by the jobs themselves.
    /// </summary>
    private static void ConfigureServicePayments(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<ServicePaymentSyncJob>(
            ServicePaymentSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServicePaymentSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ServicePaymentRemindersJob>(
            ServicePaymentRemindersJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServicePaymentRemindersJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // The money CasaZen holds (SP-15c-a): releases, retries of failed transfers, closed requests, lapsed blocks, hold warnings.
        recurringJobManager.AddOrUpdate<ServiceFundsJob>(
            ServiceFundsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceFundsJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
