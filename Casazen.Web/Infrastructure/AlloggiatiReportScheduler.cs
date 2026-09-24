using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Schedules <see cref="AlloggiatiWebReportJob"/> at the start of the arrival day in Europe/Rome (A5-03). The
/// report row, unique per booking and guest, is the idempotency key: <see cref="IAlloggiatiWebService.ReserveReportAsync"/>
/// hands out a reservation only when no job is scheduled for it, so the guest portal and the host check-in never
/// queue the job twice.
/// </summary>
public class AlloggiatiReportScheduler(
    IAlloggiatiWebService alloggiatiWebService,
    IBackgroundJobClient backgroundJobClient,
    ILogger<AlloggiatiReportScheduler> logger) : IAlloggiatiReportScheduler
{
    public async Task EnsureScheduledAsync(Guid bookingId)
    {
        var reservation = await alloggiatiWebService.ReserveReportAsync(bookingId);
        if (reservation is null)
            return;

        var guestId = reservation.GuestId;
        var jobId = backgroundJobClient.Schedule<AlloggiatiWebReportJob>(
            job => job.ReportGuestAsync(guestId, bookingId),
            new DateTimeOffset(reservation.RunAtUtc, TimeSpan.Zero));

        await alloggiatiWebService.SetScheduledJobAsync(reservation.ReportId, jobId, reservation.RunAtUtc);

        if (!string.IsNullOrEmpty(reservation.PreviousJobId) && reservation.PreviousJobId != jobId)
            backgroundJobClient.Delete(reservation.PreviousJobId);

        logger.LogInformation(
            "Alloggiati job {JobId} of booking {BookingId} scheduled for {RunAtUtc:o}",
            jobId, bookingId, reservation.RunAtUtc);
    }
}
