using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// How a host writes to the guest of a booking (E05/F02/US02/T01, NF-63): through which channel the guest can be reached now, and the first message when
/// the guest has no conversation yet. The booking is authorized as a row of its property, like the messages of a direct booking (<c>booking.read</c> to
/// ask, <c>booking.write</c> to write, and the caller's reach on the property), and a booking the caller does not reach answers 404, like a missing
/// one; the service applies the same scope in SQL. No answer carries an address or a phone number of the guest, and no log carries the words.
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class BookingGuestContactController(
    IMessageReplyService replies,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The channel through which the guest of the booking is reached now: <c>Ota</c> (the conversation the booking has on a channel, with its
    /// <c>threadKey</c>), <c>Direct</c> (the conversation of the booking site, while it is open), <c>Email</c> (a booking of a channel that has no chat, whose
    /// guest the channel manager holds an address for), or <c>None</c> with a <c>reason</c>. <c>emailAvailable</c> and <c>phoneAvailable</c> say whether an
    /// address and a number exist; they are never the address or the number, and nothing sensitive is asked of the channel manager to answer.
    /// </summary>
    [HttpGet("contact-options")]
    [ProducesResponseType(typeof(MessageContactOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageContactOptionsDto>> ContactOptions(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Read, cancellationToken))
            return NotFound();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        return Ok(MessageContactOptionsDto.From(await replies.ChooseChannelAsync(scope, id, cancellationToken)));
    }

    /// <summary>
    /// The host's first message to the guest, with the header <c>Idempotency-Key</c> (8 to 64 characters) and the body <c>{ text }</c>: through the channel
    /// <c>contact-options</c> names. For an e-mail the contact of this one booking is read from the channel manager (409 <c>contact_info_unavailable</c>
    /// when it does not release it, which it does not until CasaZen's static IP addresses are registered with it); the read is recorded in the activity log
    /// of the organization and the address goes to the queue of the e-mails and nowhere else. 200 or 202 as the reply of a conversation; 404; 409
    /// <c>no_contact_channel</c>, <c>mapping_not_confirmed</c>, <c>thread_closed</c>; 422; 429.
    /// </summary>
    [HttpPost("messages/new")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [EnableRateLimiting(RateLimitPolicies.HostMessageSend)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(MessageReplyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MessageReplyDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<MessageReplyDto>> StartConversation(
        Guid id,
        [FromBody] ReplyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Write, cancellationToken))
            return NotFound();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var result = await replies.StartConversationAsync(scope, id, request.Text, idempotencyKey, User.GetUserId(), cancellationToken);
        var answer = MessageReplyDto.From(result);
        return result.State == OtaMessageDeliveryState.ManualVerify
            ? StatusCode(StatusCodes.Status202Accepted, answer)
            : Ok(answer);
    }

    private async Task<bool> IsAuthorizedAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation);
    }

    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private ObjectResult Forbidden() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}
