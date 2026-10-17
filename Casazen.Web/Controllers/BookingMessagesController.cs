using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host's side of the messages with the guest (DB-14a), on the booking: read the thread, answer, mark it read. The booking is
/// authorized as a row of its property (<c>booking.read</c> to read, <c>booking.write</c> to answer and to mark as read, and the caller's
/// reach on the property), and a booking the caller does not reach answers 404, like a missing one. The text of the messages is the guest's
/// personal data: it never goes in a log or in an email.
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}/messages")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class BookingMessagesController(
    IGuestMessageService messages,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// The thread of the booking, oldest message first. <c>state</c> <c>Unavailable</c> for a booking that has no thread (not a direct
    /// booking, cancelled); <c>unreadCount</c> and <c>needsReply</c> tell what the host still has to read and answer.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HostThreadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostThreadDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Read, cancellationToken))
            return NotFound();

        return Ok(HostThreadDto.From(await messages.GetForHostAsync(id, cancellationToken)));
    }

    /// <summary>
    /// The host's message to the guest (also the first one), with the header <c>Idempotency-Key</c> the screen made for it. Plain text up to
    /// 2000 characters. The guest gets an email without the text, at most one every ten minutes. Answering marks the thread as read.
    /// 409 <c>thread_closed</c> when the thread is closed or the booking has none; 422 <c>guest_message_text_invalid</c> /
    /// <c>idempotency_key_invalid</c>; 404.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [EnableRateLimiting(RateLimitPolicies.HostMessageSend)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(SentGuestMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SentGuestMessageDto>> Reply(
        Guid id,
        [FromBody] SendMessageRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Write, cancellationToken))
            return NotFound();

        var sent = await messages.ReplyAsHostAsync(id, request.Text, idempotencyKey, User.GetUserId(), cancellationToken);
        return Ok(SentGuestMessageDto.From(sent));
    }

    /// <summary>Marks the guest's messages as read by the host (up to the last one written) and answers the thread as it is after.</summary>
    [HttpPost("read")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(HostThreadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostThreadDto>> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Write, cancellationToken))
            return NotFound();

        return Ok(HostThreadDto.From(await messages.MarkReadAsync(id, cancellationToken)));
    }

    private async Task<bool> IsAuthorizedAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation);
    }
}
