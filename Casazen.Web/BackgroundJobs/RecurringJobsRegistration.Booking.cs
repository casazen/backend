using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The SEO content, the direct booking charge, the rent collection, the checkout holds, the custom domains and the iCal syncs of the calendars.
    /// </summary>
    private static void RegisterBookingJobs(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<SeoContentRefreshJob>(
            "seo-content-refresh",
            job => job.ExecuteAsync(),
            "0 4 1 * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // SE-04: the events of the SEO funnel are deleted after Seo:Events:RetentionDays, nightly.
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

        // LT-06: payment requests of the rent installments coming due, payments in flight read again.
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

        // BK-17: custom domains are checked again (activation, removed DNS records, domains dropped by their host).
        recurringJobManager.AddOrUpdate<DomainRecheckJob>(
            DomainRecheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            DomainRecheckJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<IcalSupplierSyncJob>(
            "ical-supplier-sync",
            job => job.ExecuteAsync(),
            "*/15 * * * *",  // Every 15 minutes
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // SR-10b: the page of the calendars says how often this runs from the same constant, never from a number of its own.
        recurringJobManager.AddOrUpdate<PropertyICalSyncJob>(
            "property-ical-sync",
            job => job.ExecuteAsync(),
            PropertyICalSchedule.SyncCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
