using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterMaintenanceJobs(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<PushReceiptsJob>(
            PushReceiptsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PushReceiptsJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<InAppNotificationRetentionJob>(
            InAppNotificationRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            InAppNotificationRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<ApprovalExpirationJob>(
            ApprovalExpirationJob.RecurringJobId,
            job => job.ProcessExpirationsAsync(CancellationToken.None),
            ApprovalExpirationJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<PropertyComplianceCheckJob>(
            PropertyComplianceCheckJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PropertyComplianceCheckJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<FiscalRatesUpdateJob>(
            FiscalRatesUpdateJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 5 * * 1",
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

        recurringJobManager.AddOrUpdate<ComuneOfficialProfileRefreshJob>(
            ComuneOfficialProfileRefreshJob.RecurringJobId,
            job => job.ExecuteAsync(),
            ComuneOfficialProfileRefreshJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
