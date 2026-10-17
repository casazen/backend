using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guest's side of the messages with the host (DB-14a), in the guest area: the guest who proved a booking (<c>api/public/guest/session</c>)
/// reads the thread of that booking and writes to the host. Behind the policy <see cref="CasazenPolicies.GuestSession"/>; the booking is the
/// one <see cref="IGuestSessionService.FindBookingAsync"/> finds for the guest of the session, so a booking of another guest or another
/// site answers the same uniform 404 <c>guest_booking_not_found</c> as everywhere in the area, and so does a booking that was cancelled
/// or never confirmed (it has no thread). Never indexable and never cached. Runbook <c>docs/runbooks/guest-messages.md</c>.
/// </summary>
[ApiController]
[Route("api/public/guest/bookings/{code}/messages")]
[Authorize(Policy = CasazenPolicies.GuestSession)]
[PrivateAnswer]
public class GuestMessagesController(IGuestSessionService sessions, IGuestMessageService messages) : ControllerBase
{
    /// <summary>
    /// The thread of the booking, oldest message first (the latest 300), also after it closed. 401 without a valid session; 404
    /// <c>guest_booking_not_found</c>; 429.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(GuestThreadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestThreadDto>> Get(string code, CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        var booking = await sessions.FindBookingAsync(ticket, code, cancellationToken);
        return Ok(GuestThreadDto.From(await messages.GetForGuestAsync(booking, cancellationToken)));
    }

    /// <summary>
    /// A message to the host, with the header <c>Idempotency-Key</c> the screen made for it (the same key again answers the first message and
    /// writes nothing). Plain text up to 2000 characters, card numbers and IBANs replaced by «[rimosso]». The host gets an email without the
    /// text, at most one every ten minutes. 409 <c>thread_closed</c> once the thread closed (the seventh day after the check-out); 422
    /// <c>guest_message_text_invalid</c> / <c>idempotency_key_invalid</c>; 401; 404; 429.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.GuestMessageSend)]
    [GuestMessageSessionRateLimit]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(SentGuestMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SentGuestMessageDto>> Send(
        string code,
        [FromBody] SendMessageRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        var booking = await sessions.FindBookingAsync(ticket, code, cancellationToken);
        var sent = await messages.SendAsGuestAsync(booking, request.Text, idempotencyKey, cancellationToken);
        return Ok(SentGuestMessageDto.From(sent));
    }
}
