using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The daily import of the bookings of the channel manager (E02/F02/US01/T01, ADR-033-R4): for every property with a confirmed mapping, its room
/// chosen and an org that is connected, it reads the reservations Octorate lists as changed and upserts the bookings by their Octorate id.
/// Daily at 02:00 Europe/Rome, behind <c>Features:OctorateConnector</c> (the registration removes it while the flag is off).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What it reads.</b> A listing that was never read completely: its whole history (3 years). Afterwards: what changed since the last complete read,
/// two days earlier (the retry window of the webhooks), so a missed notification is caught within a day. The checkpoint is kept on the mapping
/// by <see cref="IBookingImportService"/> and moves only when a run read everything it had to.</item>
/// <item><b>Tenant.</b> A system read over every org to find the properties; each is then read for its own org under <see cref="IOctorateOrgScope.Use"/>,
/// which is what lets the client (no org parameter) act for it. A property that fails does not stop the others; an org whose connection is refused
/// is left for the next run.</item>
/// <item><b>Logs.</b> Ids and counts only. Hangfire does not retry the run: the next one reads the same window again.</item>
/// <item>The lock waits <see cref="LockTimeoutSeconds"/>, the value of <c>JobLockTimeouts.DefaultSeconds</c> (that class lives in the web project, which
/// this project cannot reference; a test keeps the two equal).</item>
/// </list>
/// </remarks>
public class OctorateBookingImportJob(
    AppDbContext db,
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    IHostEnvironment hostEnvironment,
    ILogger<OctorateBookingImportJob> logger)
{
    public const string RecurringJobId = "octorate-booking-import";

    /// <summary>Every day at 02:00; the registration gives the zone (Europe/Rome).</summary>
    public const string Cron = "0 2 * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.DefaultSeconds</c>, for the jobs that run every hour or less often.</summary>
    public const int LockTimeoutSeconds = 300;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var targets = await OctorateBookingRuns.ListTargetsAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var totals = new OctorateBookingRuns.RunTotals();

        foreach (var org in targets.GroupBy(t => t.OrgId))
        {
            using (orgScope.Use(org.Key))
            {
                foreach (var target in org)
                {
                    if (!await OctorateBookingRuns.RunOneAsync(import, target, options: null, totals, logger, cancellationToken))
                        break;
                }
            }
        }

        logger.LogInformation(
            "Octorate booking import: {Properties} properties read, {Created} bookings created, {Updated} updated, {Skipped} unchanged, {Unmapped} reservations of other rooms, {Incomplete} runs incomplete, {Failed} failed",
            totals.Properties,
            totals.Created,
            totals.Updated,
            totals.Skipped,
            totals.Unmapped,
            totals.Incomplete,
            totals.Failed);
    }
}
