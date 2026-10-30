using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring reminders of the day before the work (SP-10, SP-16d): every hour, the customers who booked a supplier from its public showcase whose
/// work is tomorrow (Europe/Rome) and whose request the supplier took get one e-mail, and the <b>suppliers</b> whose job is tomorrow — a request of
/// any context — get one e-mail and one push (unless they turned «Promemoria del giorno prima» off), at 18:00 of the day before or at the first
/// run after (<see cref="IServiceRequestReminderService"/>). <b>Registered whatever the feature flags say</b>: a booking that exists keeps its
/// reminder. Runbooks: <c>docs/runbooks/suppliers.md</c> sections 23 and 35, <c>docs/runbooks/hangfire.md</c>.
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

        if (run.Failed > 0 || run.SupplierFailed > 0)
        {
            logger.LogWarning(
                "Service request reminders: {Sent} queued to customers, {Failed} could not be queued; {SupplierSent} queued to suppliers, {SupplierFailed} could not be queued",
                run.Sent, run.Failed, run.SupplierSent, run.SupplierFailed);
        }
    }
}
