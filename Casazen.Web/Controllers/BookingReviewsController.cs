using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The reviews of one booking (E05/F06/US01/T02, NF-69, OCT-12): the one of the booking site and the one of its channel, with what each allows, and the read of the channel manager asked
/// for now. The booking is authorized as a row of its property (<c>booking.read</c> and the caller's reach on the property, like <see cref="BookingMessagesController"/>), and a booking the
/// caller does not reach answers 404, like a missing one. Every answer is private and not cached; the words of a guest and of a host are personal data and never go in a log.
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}/reviews")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class BookingReviewsController(
    IOtaReviewService reviews,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// The reviews of the booking, direct and of the channel, each with the three things the host may do (read, reply, write its own review) and why not, and what the channel of the
    /// booking allows when there is no review yet: <c>channelCapabilities</c> with <c>reason</c> <c>channel_not_integrated</c> for Booking.com, Vrbo and Expedia (no read, no reply and no
    /// simulated review). 404 <c>booking_not_found</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(BookingReviewsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingReviewsDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var scope = await AuthorizedScopeAsync(id, cancellationToken);
        if (scope.Failure is not null)
            return scope.Failure;

        return Ok(BookingReviewsDto.From(await reviews.ListForBookingAsync(scope.Scope!, id, cancellationToken)));
    }

    /// <summary>
    /// Reads the booking from the channel manager now, at most once every ten minutes for a booking: 429 <c>ota_review_refresh_too_soon</c> with <c>retryAfterSeconds</c> after that.
    /// Answers the reviews as they are after the read. 404 <c>booking_not_found</c>; 409 <c>octorate_not_connected</c> or <c>mapping_not_confirmed</c> when nothing can be read; 422
    /// <c>channel_not_integrated</c> for a booking whose channel has no integration for reviews.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(BookingReviewsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<BookingReviewsDto>> Refresh(Guid id, CancellationToken cancellationToken)
    {
        var scope = await AuthorizedScopeAsync(id, cancellationToken);
        if (scope.Failure is not null)
            return scope.Failure;

        return Ok(BookingReviewsDto.From(await reviews.RefreshAsync(scope.Scope!, id, cancellationToken)));
    }

    /// <summary>The caller's reach and the row check of the booking: the booking is authorized as a row of its property, or it is a 404 (a booking of another org is a missing one).</summary>
    private async Task<(HostScope? Scope, ActionResult? Failure)> AuthorizedScopeAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return (null, this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden"));

        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Read))
            return (null, NotFound());

        return (scope, null);
    }
}
