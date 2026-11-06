using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The reconciliation of the bookings with Octorate (E02/F02/US01/T02, ADR-031-R8, R9): every six hours it reads again what changed for every
/// property with a confirmed mapping, so a notification that was lost, or arrived after Octorate gave up retrying (two days), still reaches
/// CasaZen. Behind <c>Features:OctorateConnector</c> (the registration removes it while the flag is off).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The window.</b> <c>MODIFIED_BETWEEN</c> (Octorate has no <c>modifiedAfter</c>; the dates are Rome days), from the last complete read of
/// the listing minus two days, the retry window of the webhooks (OCT-09): consecutive windows overlap by at least two days. A listing never read
/// completely is read from the start of its history, like the daily import.</item>
/// <item><b>Compare and update.</b> The service compares each reservation with its booking by the <c>updateTime</c> it holds and applies the
/// versions that are newer: the states, the dates, the guests and the figures that differ are corrected, with their events.</item>
/// <item><b>Optimistic window.</b> A booking changed in CasaZen within the last <see cref="OptimisticWindow"/> is left as it is
/// (<see cref="BookingImportOptions.SkipChangedWithin"/>): the change a person made a moment ago has not reached Octorate yet, and an older read
/// must not undo it. The next run finds it again if it still differs.</item>
/// <item><b>Tenant.</b> A system read over every org; each property is read for its own org under <see cref="IOctorateOrgScope.Use"/>. A property
/// that fails does not stop the others. No retry by Hangfire: the next run reads the same window.</item>
/// <item>The lock waits <see cref="LockTimeoutSeconds"/>, the value of <c>JobLockTimeouts.DefaultSeconds</c> (a test keeps the two equal).</item>
/// </list>
/// </remarks>
public class OctorateBookingSyncJob(
    AppDbContext db,
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    IHostEnvironment hostEnvironment,
    ILogger<OctorateBookingSyncJob> logger)
{
    public const string RecurringJobId = "octorate-booking-sync";

    /// <summary>Every six hours, on the hour (UTC).</summary>
    public const string Cron = "0 */6 * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.DefaultSeconds</c>.</summary>
    public const int LockTimeoutSeconds = 300;

    /// <summary>A booking CasaZen changed within this time is not overwritten by the reconciliation (ADR-031-R9).</summary>
    public static readonly TimeSpan OptimisticWindow = TimeSpan.FromMinutes(5);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var targets = await OctorateBookingRuns.ListTargetsAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var options = new BookingImportOptions(OptimisticWindow);
        var totals = new OctorateBookingRuns.RunTotals();

        foreach (var org in targets.GroupBy(t => t.OrgId))
        {
            using (orgScope.Use(org.Key))
            {
                foreach (var target in org)
                {
                    if (!await OctorateBookingRuns.RunOneAsync(import, target, options, totals, logger, cancellationToken))
                        break;
                }
            }
        }

        logger.LogInformation(
            "Octorate booking reconciliation: {Properties} properties read, {Created} bookings created, {Updated} corrected, {Skipped} unchanged or protected, {Unmapped} reservations of other rooms, {Incomplete} runs incomplete, {Failed} failed",
            totals.Properties,
            totals.Created,
            totals.Updated,
            totals.Skipped,
            totals.Unmapped,
            totals.Incomplete,
            totals.Failed);
    }
}
