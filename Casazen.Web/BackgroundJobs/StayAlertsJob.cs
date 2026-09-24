using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Hourly host alerts about stays (CO-10): Alloggiati Web stages, failed communications and the check-out day reminder,
/// each sent at most once per stay (<see cref="IStayAlertService"/>). Replaces <c>alloggiati-deadline-alert</c> (hourly)
/// and <c>guest-checkin-reminder</c> (daily), which sent the same "check-in incomplete" text every hour.
/// </summary>
public class StayAlertsJob(IStayAlertService stayAlertService)
{
    public const string RecurringJobId = "stay-alerts";

    /// <summary>Recurring ids of the jobs it replaces, removed at startup.</summary>
    public static readonly string[] ReplacedRecurringJobIds = ["alloggiati-deadline-alert", "guest-checkin-reminder"];

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync() => await stayAlertService.RunAsync();
}
