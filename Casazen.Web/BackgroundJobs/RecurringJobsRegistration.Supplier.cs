using Casazen.Core.Features;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The supplier side: the supplier orders of the guest portal, the service requests (auto-cancel, groups, expiry, reminders), the service
    /// payments, the showcase media cleanup and the Octorate marketplace catalog and connections.
    /// </summary>
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

        // E09/F01/US01/T02 (ADR-023): the cache of the portal catalog of the marketplace is refreshed every day, only while the Octorate connector is on.
        ConfigureMarketplacePortalRefresh(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        // E09/F03/US01/T01 (ADR-023, NF-57): the health of the active integrations is read from Octorate every 30 minutes, only while the Octorate connector is on.
        ConfigureMarketplaceConnectionSync(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        // SP-BKP: the groups of backup suppliers are followed whatever the flag SupplierRequestBackups says (it only gates their creation): a
        // group that exists has its windows, and turning the flag off must not leave its requests hanging. With no group a run finds nothing.
        recurringJobManager.AddOrUpdate<ServiceRequestGroupJob>(
            ServiceRequestGroupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ServiceRequestGroupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // SP-10: the upkeep of the bookings from the suppliers' public showcases is registered whatever the flags say. A hold that
        // was made while SupplierShowcaseBooking was on has to lapse, a request nobody answered has to be cancelled and a booking
        // that exists keeps its reminder, also after the flag is turned off; with the flag off these runs just find nothing.
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

        // SP-13b: the images of the showcases that neither the draft, nor the published version, nor the one before it point to are removed
        // after a day, every night. Always registered: a file that exists costs money whatever a flag says; with none to remove a run finds nothing.
        recurringJobManager.AddOrUpdate<SupplierShowcaseMediaCleanupJob>(
            SupplierShowcaseMediaCleanupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            SupplierShowcaseMediaCleanupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// Automatic cancellation of the service requests nobody answered (SP-04, D8): only with
    /// <see cref="FeatureFlags.SupplierRequestAutoCancel"/> on. With the flag off no request is ever cancelled by time, and the
    /// schedule of an earlier deploy is removed.
    /// </summary>
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
