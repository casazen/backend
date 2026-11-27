using Casazen.Core.Entities;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Sends the new state of one booking to Octorate (E02/F02/US01/T02, ADR-031-R10), queued by <c>OctorateBookingStatePushHandler</c> when the host
/// registers an arrival or a departure. It runs for the org of the booking under <see cref="IOctorateOrgScope.Use"/> (the client takes no org)
/// and, since E20/F01/US01/T01, asks <see cref="ICheckInOctorateSyncService.SetStayStateAsync"/> to send it: the state has a row of its own, with
/// its attempts and its last error, and a "To do" when it keeps failing. A booking that is not mirrored sends nothing.
/// </summary>
/// <remarks>
/// <para>The signature is the one of E02 on purpose: jobs queued before this change are still in the Hangfire storage and run the new body.</para>
/// <para>A refusal or a failure of Octorate does not reach Hangfire: the state is kept <c>Failed</c> on its row and <see cref="OctorateCheckInRetryJob"/>
/// tries it again with a backoff, up to <c>Octorate:CheckInMaxAttempts</c>, then raises the "To do". An arrival that must wait for the write of the
/// guests of the stay stays <c>Pending</c> and is asked again by the same job. What Hangfire still retries is an unexpected error (the database, before
/// any row exists): three times, after one, five and fifteen minutes, then the job stays failed in the dashboard with the type of the error, never with
/// the booking's data. The state itself is CasaZen's and is already saved: a failed push never undoes the check-in or the check-out. One run per
/// booking at a time.</para>
/// </remarks>
public class OctorateBookingStatePushJob(
    ICheckInOctorateSyncService sync,
    IOctorateOrgScope orgScope,
    ILogger<OctorateBookingStatePushJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("OctorateBookingStatePushJob.PushAsync:{1}", 60)]
    public async Task PushAsync(Guid orgId, Guid bookingId, BookingStatus status, CancellationToken cancellationToken)
    {
        // The two states CasaZen owns: the arrival of a checked-in stay and the departure of a checked-out one. Any other status is nothing to send.
        OctorateStayState? state = status switch
        {
            BookingStatus.CheckedIn => OctorateStayState.CheckInEnd,
            BookingStatus.CheckedOut => OctorateStayState.CheckOutEnd,
            _ => null,
        };
        if (state is not { } stayState)
        {
            logger.LogDebug("Booking {BookingId} of org {OrgId}: no Octorate state to send for {Status}", bookingId, orgId, status);
            return;
        }

        OctorateStayStateResult result;
        using (orgScope.Use(orgId))
            result = await sync.SetStayStateAsync(bookingId, stayState, cancellationToken);

        logger.LogInformation(
            "Booking {BookingId} of org {OrgId}: state {State} to Octorate {Outcome} {ErrorCode}",
            bookingId, orgId, stayState, result.Outcome, result.ErrorCode);
    }
}
