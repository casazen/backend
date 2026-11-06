using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The mirror of a booking on the channel manager (E08/F01/US01/T03, NF-74, NF-80): the host asks again for a push that failed or was refused.
/// TN-3: the booking is authorized as a <see cref="HostResource"/> of its property for <c>booking.write</c>; a booking of another org, or one the
/// caller may not reach, answers 404 like a missing one. Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication.
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}/octorate-sync")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.BookingWrite)]
public class BookingOctorateSyncController(
    IBookingOctorateSyncService sync,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<BookingOctorateSyncController> logger) : ControllerBase
{
    /// <summary>
    /// Asks again for the push of the booking to the channel manager: only a <c>Failed</c> or <c>Conflict</c> sync (409
    /// <c>octorate_sync_not_retryable</c> otherwise). The push runs again through the event bus (the same handler as a confirmation, a reservation
    /// whose answer was lost is looked up before anything is created): 202 with the state <c>Pending</c> (queued; the row becomes <c>Pending</c>
    /// when the push starts, and stays retryable if the push gives up). <c>overwrite=true</c> is the host's
    /// choice to write CasaZen's version over a reservation that changed on Octorate, for a change (E08/F02); a create has nothing to overwrite.
    /// 404 for a booking of another org or out of the caller's reach.
    /// </summary>
    [HttpPost("retry")]
    [ProducesResponseType(typeof(OctorateSyncRetryResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OctorateSyncRetryResponse>> Retry(
        Guid id,
        [FromQuery] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var resource = await hostResources.ForBookingAsync(id, cancellationToken);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write))
            return NotFound();

        var state = await sync.RequeueAsync(id, overwrite, cancellationToken);
        logger.LogInformation("Octorate push of booking {BookingId} asked again by user {UserId}", id, User.GetUserId());
        return Accepted(new OctorateSyncRetryResponse(id, state));
    }
}
