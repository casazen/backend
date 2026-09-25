using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Arrival-day step of an Alloggiati Web communication, scheduled by <see cref="IAlloggiatiReportScheduler"/> at the
/// start of the arrival day in Europe/Rome. CasaZen does not transmit yet (CO-13): the report becomes "to send
/// manually" and the host sends it on the Questura portal.
/// </summary>
public class AlloggiatiWebReportJob(
    IAlloggiatiWebService alloggiatiWebService,
    IAlloggiatiReportScheduler scheduler,
    ILogger<AlloggiatiWebReportJob> logger)
{
    [DisableConcurrentExecution("AlloggiatiWebReportJob.ReportGuestAsync:{1}", JobLockTimeouts.DefaultSeconds)] // one submission per booking at a time
    public async Task ReportGuestAsync(Guid guestId, Guid bookingId)
    {
        var outcome = await alloggiatiWebService.ProcessScheduledReportAsync(bookingId, guestId);
        logger.LogInformation("Alloggiati job of booking {BookingId}: {Outcome}", bookingId, outcome);

        // Check-in date moved later, or a job queued before the report existed: schedule it for the arrival day.
        if (outcome is AlloggiatiProcessOutcome.NotYetDue or AlloggiatiProcessOutcome.NotReserved)
            await scheduler.EnsureScheduledAsync(bookingId);
    }
}
