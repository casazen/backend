using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The invitation of a guest to review a stay (DB-12a), at the host's explicit request: <c>POST</c> sends the one email with the signed
/// link, <c>GET</c> tells the host whether it can and what already happened. Nothing else sends it: no job, no reminder, no event. The
/// booking is authorized as a row of its property (<c>booking.read</c> to look, <c>booking.write</c> to send).
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}/review-invite")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingReviewInviteController(
    IHostReviewService reviews,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// Whether the host can ask the guest for a review now (<c>canInvite</c>), why not (<c>reason</c>), when it asked
    /// (<c>inviteSentAt</c>) and whether the guest already reviewed (<c>reviewed</c>).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ReviewInviteStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReviewInviteStatusDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Read, cancellationToken))
            return NotFound();

        return Ok(ReviewInviteStatusDto.From(await reviews.GetInviteStatusAsync(id, cancellationToken)));
    }

    /// <summary>
    /// Sends the guest the one email with the link to write the review: only for a booking made through the site, not cancelled, whose
    /// stay ended no more than 14 days ago, with the guest's address (422 <c>review_invite_not_allowed</c> otherwise), once per booking
    /// (409 <c>review_invite_already_sent</c>) and not when the review exists (409 <c>review_already_submitted</c>). Answers the state
    /// after the call. This email leaves from this call only.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(ReviewInviteStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ReviewInviteStatusDto>> Send(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Write, cancellationToken))
            return NotFound();

        await reviews.SendInviteAsync(id, User.GetUserId(), cancellationToken);
        return Ok(ReviewInviteStatusDto.From(await reviews.GetInviteStatusAsync(id, cancellationToken)));
    }

    private async Task<bool> IsAuthorizedAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation);
    }
}
