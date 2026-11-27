using Casazen.Core.Utilities;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The seasonal suggestions and the GDPR retention, then the compliance jobs: the stay alerts, the monthly summary, the retention of the proofs,
    /// the purge of the guest documents, the Swiss retention and the CIN alert.
    /// </summary>
    private static void RegisterComplianceJobs(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<DynamicPricingJob>(
            DynamicPricingJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "0 2 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<GdprDataRetentionJob>(
            GdprDataRetentionJob.RecurringJobId,
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

        // E03/F02/US02/T02 (NF-52, CHK-12): the monthly compliance summary of every active short-rent org, on the first of the month at 07:00 Europe/Rome.
        // Always registered: the service reads Features:ComplianceTrafficLight at each run and sends nothing while it is off.
        recurringJobManager.AddOrUpdate<ComplianceMonthlySummaryJob>(
            ComplianceMonthlySummaryJob.RecurringJobId,
            job => job.ExecuteAsync(),
            ComplianceMonthlySummaryJob.Cron,
            new RecurringJobOptions { TimeZone = RomeCalendar.TimeZone });

        // E20/F02/US01/T01 (NF-47, CHK-08, AC-NF52-12): the proofs of the compliance duties are kept five years from their upload; the file and the row of those whose
        // time is over are deleted every day at 03:40 UTC, counts only in the log. Always registered: a file that exists costs money whatever a flag says, and with no
        // proof past its time a run finds nothing.
        recurringJobManager.AddOrUpdate<ComplianceProofRetentionJob>(
            ComplianceProofRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ComplianceProofRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E20/F02/US01/T02 (NF-121, NF-16, decision O4): once a stay is covered by an archived Alloggiati receipt the photos of its guests' identity documents and the legacy scan of
        // its booker are deleted, every day at 03:50 UTC (and by a run for the organization queued right after a receipt upload). Always registered: the service reads
        // Features:PurgeDocumentsAfterReceipt at each run and reads and deletes nothing while it is off (the deletion cannot be undone).
        recurringJobManager.AddOrUpdate<GuestDocumentPurgeJob>(
            GuestDocumentPurgeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            GuestDocumentPurgeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E73/F01/US02/T03 (NF-195, D9): the retention of what the identity verification keeps, every hour at minute 40 UTC: the evidence of the reviewers past its time, the abandoned
        // sessions, the kept pictures and the checks of the document and the verification logs past their retention. Always registered: the service reads the flags at each run, and with
        // both Features:CheckInIdentitySelfHosted and Features:CheckInIdentityReviewEvidence off it deletes only the evidence past its time (a flag switched off must never leave a
        // biometric picture with no expiry); the other categories wait for a flag.
        recurringJobManager.AddOrUpdate<IdentityEvidencePurgeJob>(
            IdentityEvidencePurgeJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            IdentityEvidencePurgeJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // NF-128 (casazen/backend#979, decisions D10, D11): the receipts of the Ticino police notifications, the group sheets and the parents' authorizations are kept
        // five years from their date; the file and the row of those whose time is over are deleted every day at 03:55 UTC, counts only in the log, never an OTR report.
        // Always registered: a file that exists costs money whatever a flag says, and with nothing past its time a run finds nothing.
        recurringJobManager.AddOrUpdate<SwissComplianceRetentionJob>(
            SwissComplianceRetentionJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            SwissComplianceRetentionJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // CO-20: daily CIN alert, after the nightly compliance check of CO-06.
        recurringJobManager.AddOrUpdate<CinDeadlineAlertJob>(
            CinDeadlineAlertJob.RecurringJobId,
            job => job.ExecuteAsync(),
            CinDeadlineAlertJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
