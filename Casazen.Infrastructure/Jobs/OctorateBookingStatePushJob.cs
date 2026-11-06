using Casazen.Core.Entities;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Sends the new state of one booking to Octorate (E02/F02/US01/T02, ADR-031-R10), queued by <c>OctorateBookingStatePushHandler</c> when the host
/// registers an arrival or a departure. It runs for the org of the booking under <see cref="IOctorateOrgScope.Use"/> (the client takes no org)
/// and calls <see cref="IBookingImportService.SyncBookingStateAsync"/>, which sends nothing for a booking that is not mirrored.
/// </summary>
/// <remarks>
/// One run per booking at a time. A failure of Octorate is tried again three times, after one, five and fifteen minutes; then the job stays
/// failed in the Hangfire dashboard with the type of the error, never with the booking's data. The state itself is CasaZen's and is already
/// saved: a failed push never undoes the check-in or the check-out.
/// </remarks>
public class OctorateBookingStatePushJob(
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    ILogger<OctorateBookingStatePushJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("OctorateBookingStatePushJob.PushAsync:{1}", 60)]
    public async Task PushAsync(Guid orgId, Guid bookingId, BookingStatus status, CancellationToken cancellationToken)
    {
        using (orgScope.Use(orgId))
            await import.SyncBookingStateAsync(bookingId, status, cancellationToken);

        logger.LogInformation("Booking {BookingId} of org {OrgId}: state {Status} sent to Octorate", bookingId, orgId, status);
    }
}
