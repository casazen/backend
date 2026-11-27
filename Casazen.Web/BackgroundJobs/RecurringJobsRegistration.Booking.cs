using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterBookingJobs(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<SeoContentRefreshJob>(
            "seo-content-refresh",
            job => job.ExecuteAsync(),
            "0 4 1 * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<SeoEventRetentionJob>(
            SeoEventRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "30 3 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<DirectBookingChargeJob>(
            "direct-booking-charge",
            job => job.ExecuteAsync(),
            "0 6 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<RentCollectionJob>(
            RentCollectionJob.RecurringJobId,
            job => job.ExecuteAsync(),
            RentCollectionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CheckoutHoldExpiryJob>(
            CheckoutHoldExpiryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CheckoutHoldExpiryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<DomainRecheckJob>(
            DomainRecheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            DomainRecheckJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<IcalSupplierSyncJob>(
            "ical-supplier-sync",
            job => job.ExecuteAsync(),
            "*/15 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<PropertyICalSyncJob>(
            "property-ical-sync",
            job => job.ExecuteAsync(),
            PropertyICalSchedule.SyncCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
