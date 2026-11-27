using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterChannelJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        ConfigureOtaPartnerJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OtaPartnerApi));

        ConfigureOctorateJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateConnector));

        ConfigureOctorateDocumentJobs(
            recurringJobManager,
            featureFlags.IsEnabled(FeatureFlags.OctorateConnector) && featureFlags.IsEnabled(FeatureFlags.OctorateDocumentImport));
    }

    /// <summary>
    /// The jobs of the Octorate connector (epic E02): only with <see cref="FeatureFlags.OctorateConnector"/> on. With the flag off no booking is
    /// read and no calendar is synced, and the schedule of an earlier deploy is removed (<c>RemoveIfExists</c>), as for the OTA partner jobs.
    /// <list type="bullet">
    /// <item>the daily import of the bookings, at 02:00 Europe/Rome (E02/F02/US01/T01, ADR-033-R4);</item>
    /// <item>the reconciliation of the bookings every six hours (E02/F02/US01/T02, ADR-031-R8);</item>
    /// <item>the sync of the calendar every 15 minutes (E02/F02/US01/T03, ADR-002-R2, NF-36), next to the iCal sync of the feeds
    /// (<c>property-ical-sync</c>), which stays registered as it is;</item>
    /// <item>the retry of the check-in writes (guests and states of the stay) every 15 minutes (E03/F01/US01/T03);</item>
    /// <item>the pass over the OTA conversations every 10 minutes and the deep pass at 02:30 UTC (epic E05, E05/F01/US01/T03, ADR-032, OCT-09).</item>
    /// </list>
    /// </summary>
    private static void ConfigureOctorateJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OctorateBookingImportJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OctorateBookingSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(CalendarSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OctorateCheckInRetryJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OtaMessageReconcileJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OtaMessageDeepReconcileJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(OtaReviewSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ListingStatusSyncJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ListingChannelHealthJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ListingOutsideChangeJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(CalendarChangeProcessJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OctorateCheckInRetryJob>(
            OctorateCheckInRetryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateCheckInRetryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OctorateBookingImportJob>(
            OctorateBookingImportJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateBookingImportJob.Cron,
            new RecurringJobOptions { TimeZone = RomeCalendar.TimeZone });

        recurringJobManager.AddOrUpdate<OctorateBookingSyncJob>(
            OctorateBookingSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateBookingSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<CalendarSyncJob>(
            CalendarSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CalendarSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OtaMessageReconcileJob>(
            OtaMessageReconcileJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OtaMessageReconcileJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OtaMessageDeepReconcileJob>(
            OtaMessageDeepReconcileJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OtaMessageDeepReconcileJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OtaReviewSyncJob>(
            OtaReviewSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OtaReviewSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ListingStatusSyncJob>(
            ListingStatusSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ListingStatusSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ListingChannelHealthJob>(
            ListingChannelHealthJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ListingChannelHealthJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ListingOutsideChangeJob>(
            ListingOutsideChangeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ListingOutsideChangeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E08/F05/US02/T02 (NF-36, NF-78): the changes of the calendar that are due (a postponement for quota, a back-off, a retry) are sent, and what the channel manager
        // accepted is followed until it confirms it, every minute; the run enqueued right after a change is the fast path and this is the safety net.
        recurringJobManager.AddOrUpdate<CalendarChangeProcessJob>(
            CalendarChangeProcessJob.RecurringJobId,
            job => job.RunDueAsync(CancellationToken.None),
            CalendarChangeProcessJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The import of the documents Octorate issued (E06/F03/US02/T01), every 30 minutes, only with <see cref="FeatureFlags.OctorateConnector"/> and
    /// <see cref="FeatureFlags.OctorateDocumentImport"/> on; otherwise the schedule of an earlier deploy is removed.
    /// </summary>
    private static void ConfigureOctorateDocumentJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OctorateDocumentImportJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OctorateDocumentImportJob>(
            OctorateDocumentImportJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OctorateDocumentImportJob.Cron,
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
