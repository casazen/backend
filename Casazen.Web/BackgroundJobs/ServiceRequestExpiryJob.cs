using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring upkeep of the bookings from the suppliers' public showcases (SP-10): the holds past their expiry are deleted (the
/// slot is free again, the customer's data are gone), and the showcase requests nobody answered in time move to <c>Annullato</c>
/// with the customer and the supplier told (<see cref="IServiceRequestExpiryService"/>). <b>Registered whatever the feature flags
/// say</b>: a booking made while <c>SupplierShowcaseBooking</c> was on has to lapse even if the flag is turned off afterwards, and
/// with the flag off a run only finds nothing. It does not duplicate <see cref="ServiceRequestAutoCancelJob"/>, which stays behind
/// its own flag for the requests of the hosts. Runbooks: <c>docs/runbooks/suppliers.md</c> section 23, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class ServiceRequestExpiryJob(IServiceRequestExpiryService expiryService, ILogger<ServiceRequestExpiryJob> logger)
{
    public const string RecurringJobId = "service-request-expiry";

    /// <summary>Every 5 minutes: a hold is deleted, and a request cancelled, at most 5 minutes after its time.</summary>
    public const string Cron = "*/5 * * * *";

    // One run at a time; a second run started anyway (a retry, the dashboard) waits for the first and then finds nothing due.
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await expiryService.RunAsync(cancellationToken);
        if (run.Skipped)
        {
            logger.LogInformation("Service request expiry not run: another run is in progress");
            return;
        }

        if (run.Failed > 0)
        {
            logger.LogWarning(
                "Service request expiry: {Holds} holds deleted, {Cancelled} requests cancelled, {Conflicts} changed under the run, {Failed} failed (retried by the next run)",
                run.HoldsDeleted, run.RequestsCancelled, run.Conflicts, run.Failed);
        }
    }
}
