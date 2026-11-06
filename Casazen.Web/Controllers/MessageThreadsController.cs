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
/// One conversation of the hub of messages (E05/F02/US01/T01, ADR-032, NF-61, NF-64): read it with its booking card and the link to the customer,
/// mark it read, and open the file of one of its messages. The key of a conversation (<c>ota:{id}</c> or <c>direct:{bookingId}</c>) is the one the
/// list gave; a malformed one is a 400 <c>thread_key_invalid</c>. What a caller reaches follows its <see cref="HostScope"/> in SQL: a conversation of
/// another org or out of the reach of a collaborator «Solo alcuni» is a 404, a caller without the permission is refused by the policy (403), and a
/// conversation of a channel with no confirmed property mapping is read-only for the screens (<c>allowedActions</c>) and is seen only by a caller who
/// reaches every property. The words of the messages are the guest's personal data: they never go in a log, and every answer is private and not cached.
/// The host answers a conversation with <see cref="Reply"/> (E05/F02/US02/T01), through the same scope.
/// </summary>
/// <remarks>The service of the reply is an optional constructor parameter at the end, so a test that builds the controller for the reads is untouched.</remarks>
[ApiController]
[Route("api/messages")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class MessageThreadsController(
    IMessageHubService hub,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService,
    IMessageReplyService? replies = null) : ControllerBase
{
    /// <summary>
    /// The conversation: its latest messages oldest first, the booking card (stay, guests, state and, for a caller who may read payments, the amounts
    /// in the currency of the property), the customer link (<c>{ guestId }</c>, for every conversation that has a booking) and the actions its channel
    /// allows. 400 <c>thread_key_invalid</c>; 404 <c>thread_not_found</c>.
    /// </summary>
    [HttpGet("threads/{threadKey}")]
    [ProducesResponseType(typeof(MessageThreadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageThreadDto>> Get(string threadKey, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(threadKey, out var key))
            return InvalidKey();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        // The money of the card is read only by a caller who may read payments (SR-D7), like the amounts of the booking itself.
        var includeAmounts = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.PaymentRead)).Succeeded;
        return Ok(MessageThreadDto.From(await hub.GetThreadAsync(scope, key, includeAmounts, cancellationToken)));
    }

    /// <summary>
    /// Marks the messages of the guest of the conversation as read by the host and answers what is still unread (nothing). A direct conversation also
    /// clears the caller's notifications of it. 400 <c>thread_key_invalid</c>; 404 <c>thread_not_found</c>.
    /// </summary>
    [HttpPost("threads/{threadKey}/read")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(MessageReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageReadDto>> MarkRead(string threadKey, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(threadKey, out var key))
            return InvalidKey();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        return Ok(MessageReadDto.From(await hub.MarkReadAsync(scope, key, User.GetUserId(), cancellationToken)));
    }

    /// <summary>
    /// The host's answer to a conversation (E05/F02/US02/T01, NF-63), with the header <c>Idempotency-Key</c> (8 to 64 characters) the screen made for it. A
    /// conversation with the guest of a direct booking goes through the existing direct service; one of a channel is sent to the channel by the channel
    /// manager. 200 <c>{ message, state, replayed }</c> (a message that is <c>Error</c> carries its <c>errorCode</c>), 202 when the outcome is not known
    /// (<c>ManualVerify</c>: it is checked on the channel before it is sent again, never by this call); 400 <c>thread_key_invalid</c>; 404
    /// <c>thread_not_found</c>; 409 <c>thread_closed</c>, <c>mapping_not_confirmed</c> (nothing is sent, OCT-04); 422 <c>message_text_invalid</c>,
    /// <c>channel_rule_violation</c>, <c>idempotency_key_invalid</c>; 429 <c>rate_limited</c>. The text is never logged.
    /// </summary>
    [HttpPost("threads/{threadKey}/reply")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [EnableRateLimiting(RateLimitPolicies.HostMessageSend)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(MessageReplyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MessageReplyDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<MessageReplyDto>> Reply(
        string threadKey,
        [FromBody] ReplyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(threadKey, out var key))
            return InvalidKey();

        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var result = await Replies().ReplyAsync(
            scope, key, request.Text, idempotencyKey, User.GetUserId(), MessageOrigin.Person, null, cancellationToken);
        var answer = MessageReplyDto.From(result);
        return result.State == OtaMessageDeliveryState.ManualVerify ? StatusCode(StatusCodes.Status202Accepted, answer) : Ok(answer);
    }

    /// <summary>
    /// The file of an OTA message, through this proxy and nowhere else: the row is authorized against the caller's org and scope (404 otherwise), the
    /// server fetches the address it holds (https, 30 seconds, 20 MB) and answers with the file, never with that address. Only <c>image/jpeg</c>,
    /// <c>image/png</c> and <c>application/octet-stream</c> are served, the last as a download; the answer is private, not cached and not sniffed.
    /// A file that is gone, expired, too big, of another type or unreachable is a 404 <c>attachment_unavailable</c> (the screen says "attachment no
    /// longer available").
    /// </summary>
    [HttpGet("{messageId:guid}/attachment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Attachment(Guid messageId, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return Forbidden();

        var file = await hub.OpenAttachmentAsync(scope, messageId, cancellationToken);

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return file.AsDownload
            ? File(file.Content, file.ContentType, "attachment")
            : File(file.Content, file.ContentType);
    }

    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private IMessageReplyService Replies() =>
        replies ?? throw new InvalidOperationException("The hub of messages is not registered (AddMessagesHub).");

    private ObjectResult InvalidKey() =>
        this.ApiProblem(StatusCodes.Status400BadRequest, MessageThreadKey.InvalidCode, "ThreadKeyInvalid");

    private ObjectResult Forbidden() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}
