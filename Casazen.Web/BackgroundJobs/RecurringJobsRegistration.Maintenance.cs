using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The push receipts, the retention of the in-app notifications, the approval expiry, the nightly compliance check of the properties and the
    /// refresh of the official reference data and fiscal rates.
    /// </summary>
    private static void RegisterMaintenanceJobs(IRecurringJobManager recurringJobManager)
    {
        // MO-04: Expo receipts of the pushes (DeviceNotRegistered removes the device), purge of old delivery rows.
        recurringJobManager.AddOrUpdate<PushReceiptsJob>(
            PushReceiptsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PushReceiptsJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // UI-12a: the in-app notifications older than 90 days are deleted every night.
        recurringJobManager.AddOrUpdate<InAppNotificationRetentionJob>(
            InAppNotificationRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            InAppNotificationRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // NF-06: approval queue items pending for >7 days are expired every night.
        recurringJobManager.AddOrUpdate<ApprovalExpirationJob>(
            ApprovalExpirationJob.RecurringJobId,
            job => job.ProcessExpirationsAsync(CancellationToken.None),
            ApprovalExpirationJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-06: nightly compliance check of every published property (suspends the ones that lost a requirement).
        recurringJobManager.AddOrUpdate<PropertyComplianceCheckJob>(
            PropertyComplianceCheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyComplianceCheckJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-18 (PO 2026-10-08): placeholder — runs weekly to check/update official fiscal rates when a source is configured.
        recurringJobManager.AddOrUpdate<FiscalRatesUpdateJob>(
            FiscalRatesUpdateJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 5 * * 1",  // every Monday at 05:00 UTC
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E06/F04/US01/T02 (FIS-05, NF-85): what Stripe kept and credited for the settled card payments, every 15 minutes, 50 payments a run.
        // Always registered: without a Stripe key the job reads nothing and counts no attempt.
        recurringJobManager.AddOrUpdate<PaymentProviderFeeJob>(
            PaymentProviderFeeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PaymentProviderFeeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // RS-6, RS-7, CO-12: daily check of the official ISTAT comuni file, the Alloggiati tables and the tourist-tax pages.
        recurringJobManager.AddOrUpdate<OfficialReferenceDataRefreshJob>(
            OfficialReferenceDataRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(),
            OfficialReferenceDataRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // MEF nuova_at profiles of comuni that already have a house: monthly delta, never a full Italy scrape.
        recurringJobManager.AddOrUpdate<ComuneOfficialProfileRefreshJob>(
            ComuneOfficialProfileRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(),
            ComuneOfficialProfileRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
