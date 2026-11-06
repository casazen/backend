using Casazen.Core.Entities;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Sends one state of a stay (arrival, departure, no-show) to the check-in of its reservation on Octorate (E03/F01/US01/T03, NF-45), queued by
/// <c>OctorateStayStateHandler</c> and, for the no-show, by the check-in endpoints. It runs for the org of the booking under
/// <see cref="IOctorateOrgScope.Use"/> (the client takes no org) and calls <see cref="ICheckInOctorateSyncService.SetStayStateAsync"/>, which
/// sends a state once, writes nothing for a booking that is not mirrored and keeps a failure on its row.
/// </summary>
/// <remarks>
/// No retry by Hangfire: a failed state is kept <c>Failed</c> on its row and retried by <see cref="OctorateCheckInRetryJob"/> with a backoff, up
/// to <c>Octorate:CheckInMaxAttempts</c>, then raised as one "To do". One run per booking at a time.
/// </remarks>
public class OctorateStayStateJob(
    ICheckInOctorateSyncService sync,
    IOctorateOrgScope orgScope,
    ILogger<OctorateStayStateJob> logger)
{
    /// <summary>The seconds a run waits for another run of the same booking.</summary>
    public const int LockTimeoutSeconds = 60;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution("OctorateStayStateJob.RunAsync:{1}", LockTimeoutSeconds)]
    public async Task RunAsync(Guid orgId, Guid bookingId, OctorateStayState state, CancellationToken cancellationToken)
    {
        OctorateStayStateResult result;
        using (orgScope.Use(orgId))
            result = await sync.SetStayStateAsync(bookingId, state, cancellationToken);

        logger.LogInformation(
            "Booking {BookingId} of org {OrgId}: check-in state {State} to Octorate {Outcome} {ErrorCode}",
            bookingId, orgId, state, result.Outcome, result.ErrorCode);
    }
}
