using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/bookings/{id:guid}/review-invite")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingReviewInviteController(
    IHostReviewService reviews,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
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
    /// Sends the guest the one email with the link to write the review: for a stay eligible for a guest portal review (only site bookings when the portal is off), whose
    /// stay ended no more than 14 days ago, with the guest's address (422 <c>review_invite_not_allowed</c> otherwise), once per booking
    /// (409 <c>review_invite_already_sent</c>, also when a routine sent it) and not when the review exists (409 <c>review_already_submitted</c>).
    /// Answers the state after the call. This email leaves from this call or from the routine action <c>RequestReview</c> only.
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
