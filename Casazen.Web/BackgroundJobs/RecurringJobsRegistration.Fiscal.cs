using Casazen.Core.Features;
using Casazen.Core.Utilities;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The fiscal documents of the stays that ended, the status of the electronic invoices sent to the SDI and the monthly package of the accountant.
    /// </summary>
    private static void RegisterFiscalJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        // E12/F01/US01/T02: the drafts of the stays that ended and their automatic issue, only while the issuance of the fiscal documents is on.
        ConfigureFiscalDocumentJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance));

        // E17/F01/US02/T01: the status of the electronic invoices submitted to the SDI, the fallback of the provider's webhook, only while the issuance and the SDI submission are both on.
        ConfigureSdiJobs(
            recurringJobManager,
            featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance) && featureFlags.IsEnabled(FeatureFlags.SdiSubmission));

        // E15/F01/US02/T02 (NF-101): the package of the previous month goes to the accountant of every org that asked for it, only while the package of the accountant is on.
        ConfigureAccountantPackageJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.AccountantPackage));
    }

    /// <summary>
    /// The documents of the stays that ended (E12/F01/US01/T02, NF-83, NF-112): the drafts, and their issue when the profile issues without a person, every day at 05:10
    /// Europe/Rome, only with <see cref="FeatureFlags.FiscalDocumentIssuance"/> on. With the flag off nothing is drafted or issued by time and the schedule of an earlier deploy is
    /// removed (<c>RemoveIfExists</c>). The same flag schedules the drafts of the self-billed invoices (FIX/BE-TD17-ALERTS, FIX/BE-TD17-JOB-EXIT), <b>every day from the 5th to the 14th</b> at 07:30 Europe/Rome
    /// (<see cref="SelfBillingMonthlyDraftsJob.Cron"/>: idempotent, it only makes the missing drafts and never issues), which do nothing when <c>SelfBilling:MonthlyDrafts:Enabled</c> is off (on by default,
    /// read at each run).
    /// </summary>
    private static void ConfigureFiscalDocumentJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(FiscalDocumentsDueJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(SelfBillingMonthlyDraftsJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<FiscalDocumentsDueJob>(
            FiscalDocumentsDueJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            FiscalDocumentsDueJob.Cron,
            new RecurringJobOptions { TimeZone = RomeCalendar.TimeZone });

        recurringJobManager.AddOrUpdate<SelfBillingMonthlyDraftsJob>(
            SelfBillingMonthlyDraftsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            SelfBillingMonthlyDraftsJob.Cron,
            new RecurringJobOptions { TimeZone = RomeCalendar.TimeZone });
    }

    /// <summary>
    /// The status of the electronic invoices submitted to the SDI (E17/F01/US02/T01, NF-116), every 30 minutes: only with <see cref="FeatureFlags.FiscalDocumentIssuance"/> and
    /// <see cref="FeatureFlags.SdiSubmission"/> on (and the run does nothing while no provider is configured). With either off nothing is asked and the schedule of an earlier deploy is
    /// removed (<c>RemoveIfExists</c>).
    /// </summary>
    private static void ConfigureSdiJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(SdiStatusPollJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<SdiStatusPollJob>(
            SdiStatusPollJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            SdiStatusPollJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The monthly package of the accountant (E15/F01/US02/T02, NF-101): every day at 06:00 Europe/Rome, only with <see cref="FeatureFlags.AccountantPackage"/> on. Each run sends the previous month to the accountant of
    /// every org whose monthly send is on and whose day has come, once for each org and month, and deletes the packages whose link has expired. With the flag off nothing is made or sent by time and the schedule of an
    /// earlier deploy is removed (<c>RemoveIfExists</c>).
    /// </summary>
    private static void ConfigureAccountantPackageJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(AccountantPackageMonthlyJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<AccountantPackageMonthlyJob>(
            AccountantPackageMonthlyJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            AccountantPackageMonthlyJob.Cron,
            new RecurringJobOptions { TimeZone = RomeCalendar.TimeZone });
    }
}
