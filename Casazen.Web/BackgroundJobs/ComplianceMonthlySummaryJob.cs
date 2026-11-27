using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The monthly compliance summary (E03/F02/US02/T02, NF-52, CHK-12 point 6): on the first of every month at 07:00 Europe/Rome each active short-rent
/// organization gets one email about the month that has just ended, «October: 42 stays, all compliant» or the list of what is missing. It only calls
/// <see cref="IComplianceMonthlySummaryService"/>, which decides, deduplicates (one row of <c>ComplianceMonthlySummaryLogs</c> per organization and month) and does
/// nothing while <c>Features:ComplianceTrafficLight</c> is off. Runbooks: <c>docs/runbooks/hangfire.md</c>, <c>docs/runbooks/email.md</c>.
/// </summary>
public class ComplianceMonthlySummaryJob(IComplianceMonthlySummaryService summaryService, TimeProvider? timeProvider = null)
{
    public const string RecurringJobId = "compliance-monthly-summary";

    /// <summary>07:00 on the first day of every month, in the time zone of Europe/Rome (the job is registered with <see cref="RomeCalendar.TimeZone"/>).</summary>
    public const string Cron = "0 7 1 * *";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await summaryService.RunAsync(_clock.TodayInRomeAsDateOnly(), CancellationToken.None);
}
