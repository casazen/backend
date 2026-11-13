using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Hourly host alerts about stays (CO-10): Alloggiati Web stages, failed communications and the check-out day reminder,
/// each sent at most once per stay (<see cref="IStayAlertService"/>). Replaces <c>alloggiati-deadline-alert</c> (hourly)
/// and <c>guest-checkin-reminder</c> (daily), which sent the same "check-in incomplete" text every hour. After the stay alerts it runs the alerts
/// about a property that are not tied to one stay (the ISTAT and tourist tax periods, the connection to the channel manager,
/// <see cref="IComplianceAlertService"/>, E03/F02/US02/T01).
/// </summary>
public class StayAlertsJob(IStayAlertService stayAlertService, IComplianceAlertService? complianceAlertService = null)
{
    public const string RecurringJobId = "stay-alerts";

    /// <summary>Recurring ids of the jobs it replaces, removed at startup.</summary>
    public static readonly string[] ReplacedRecurringJobIds = ["alloggiati-deadline-alert", "guest-checkin-reminder"];

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var result = await stayAlertService.RunAsync();

        // A run that found another one in progress (the advisory lock of the stay alerts) leaves everything to it, the compliance alerts included.
        if (complianceAlertService is not null && !result.Skipped)
            await complianceAlertService.RunAsync();
    }
}
