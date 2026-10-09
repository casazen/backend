using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring reminder to the customers who booked a supplier from its public showcase (SP-10): every hour, the customers whose
/// work is tomorrow (Europe/Rome) and whose request the supplier took get one e-mail, at 18:00 of the day before or at the first
/// run after (<see cref="IServiceRequestReminderService"/>). <b>Registered whatever the feature flags say</b>: a booking that
/// exists keeps its reminder. Runbooks: <c>docs/runbooks/suppliers.md</c> section 23, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class ServiceRequestReminderJob(IServiceRequestReminderService reminderService, ILogger<ServiceRequestReminderJob> logger)
{
    public const string RecurringJobId = "service-request-reminders";

    // One run at a time; a second run started anyway (a retry, the dashboard) waits for the first and then finds nothing due.
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await reminderService.SendDueAsync(cancellationToken);
        if (run.Skipped)
        {
            logger.LogInformation("Service request reminders not run: another run is in progress");
            return;
        }

        if (run.Failed > 0)
            logger.LogWarning("Service request reminders: {Sent} queued, {Failed} could not be queued", run.Sent, run.Failed);
    }
}
