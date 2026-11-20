using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The reviews of the channels (E05/F06/US01/T02, NF-69, OCT-12, OCT-13): one review, the host's reply to it and the host's own review of the guest. Airbnb only: Booking.com, Vrbo and
/// Expedia have no integration for reviews and no review of theirs is ever read or simulated. Reading needs <c>booking.read</c>, the two sends <c>booking.write</c>; the review is
/// authorized as a row of its property (the caller's reach on it, a collaborator «Solo alcuni» included), and a review of another org or out of the reach answers 404, like a missing one. The
/// sends carry the header <c>Idempotency-Key</c> (8 to 64 characters): the same key sends nothing again, and a call whose outcome is not known is never sent again by the call. Every answer is
/// private and not cached; the words of a guest and of a host are personal data and never go in a log.
/// </summary>
[ApiController]
[Route("api/reviews/ota")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class OtaReviewsController(
    IOtaReviewService reviews,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>One review of a channel: the guest's review, the reply, the host's own review, the state of the read and what each action allows. 404 <c>ota_review_not_found</c>.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OtaReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OtaReviewDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (!await IsAuthorizedAsync(id, BookingOperations.Read, cancellationToken))
            return NotFound();

        return Ok(OtaReviewDto.From(await reviews.GetAsync(scope, id, cancellationToken)));
    }

    /// <summary>
    /// The host's reply to the guest's review, once, with the header <c>Idempotency-Key</c> the screen made for it. 200 <c>{ review, state, replayed, errorCode }</c> (a reply that is
    /// <c>Error</c> carries its <c>errorCode</c>); 202 when the outcome is not known (<c>ManualVerify</c>: the next read of the booking finds it on the channel, or turns it into an error
    /// the host can try again after, with a new key; never sent again by this call); 404 <c>ota_review_not_found</c>; 409 <c>mapping_not_confirmed</c>,
    /// <c>ota_review_reply_already_sent</c>, <c>ota_review_action_unconfirmed</c>, <c>ota_review_action_not_allowed</c>; 422 <c>ota_review_channel_not_supported</c>,
    /// <c>ota_review_not_published</c>, <c>review_reply_invalid</c> (1 to 800 characters), <c>idempotency_key_invalid</c>; 429 <c>rate_limited</c>. The text is never logged.
    /// </summary>
    [HttpPost("{id:guid}/reply")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [EnableRateLimiting(RateLimitPolicies.HostMessageSend)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(OtaReviewActionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OtaReviewActionDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OtaReviewActionDto>> Reply(
        Guid id,
        [FromBody] ReplyOtaReviewRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Write, cancellationToken))
            return NotFound();

        var result = await reviews.ReplyAsync(id, request.Text, idempotencyKey, User.GetUserId(), cancellationToken);
        return Answer(result);
    }

    /// <summary>
    /// The host's own review of the guest (<c>{ ratings: [{ category, value }], text, privateText? }</c>), once, while the window is open. The same header, answers and rules as the reply;
    /// 409 <c>ota_review_window_closed</c> when the window is not open, 422 <c>ota_review_host_review_invalid</c> for no rating, a value that is not a whole number from 1 to 5, or no text.
    /// </summary>
    [HttpPost("{id:guid}/host-review")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [EnableRateLimiting(RateLimitPolicies.HostMessageSend)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(OtaReviewActionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OtaReviewActionDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OtaReviewActionDto>> SendHostReview(
        Guid id,
        [FromBody] SendHostReviewRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Write, cancellationToken))
            return NotFound();

        var result = await reviews.SendHostReviewAsync(id, request.ToCommand(), idempotencyKey, User.GetUserId(), cancellationToken);
        return Answer(result);
    }

    private ActionResult<OtaReviewActionDto> Answer(OtaReviewActionResult result)
    {
        var answer = OtaReviewActionDto.From(result);
        return result.State == OtaReviewActionState.ManualVerify ? StatusCode(StatusCodes.Status202Accepted, answer) : Ok(answer);
    }

    // The review is authorized as a row of its property: org, permission and the caller's reach on the property.
    private async Task<bool> IsAuthorizedAsync(Guid reviewId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var propertyId = await reviews.FindPropertyIdAsync(reviewId, cancellationToken);
        var resource = propertyId is null ? null : await hostResources.ForPropertyAsync(propertyId.Value, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation);
    }
}
