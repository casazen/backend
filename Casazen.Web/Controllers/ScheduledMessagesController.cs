using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The scheduled messages of a booking (E05/F07/US01, NF-71, MSG-11): the timeline of what its routines will send, and what the host does with each message: edit it for this booking only, skip it or send it now.
/// Behind <c>Features:ScheduledMessages</c>: 404 before authentication while it is off. The booking is authorized as a row of its property, like the messages of a direct booking (<c>booking.read</c> to read,
/// <c>booking.write</c> to change, and the caller's reach on the property); a message of another org or out of the reach of the caller answers 404, like a missing one, and the service applies the same scope in
/// SQL. The answers are private and never cached: they carry the words of messages written for a guest, and no link of the check-in and no access code (a preview never issues or releases them).
/// </summary>
[ApiController]
[FeatureGate(FeatureFlags.ScheduledMessages)]
[PrivateAnswer]
public class ScheduledMessagesController(
    IScheduledMessageService messages,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The timeline of the booking in time order, each message with its planned time and the zone of the property, the routine, the state, the reason it is blocked or skipped, whether it was edited, the channel it will
    /// use and the text as it will read, with the variables the booking has no value for. 404 for a booking out of the reach of the caller.
    /// </summary>
    [HttpGet("api/bookings/{id:guid}/scheduled-messages")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ScheduledMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ScheduledMessageDto>>> List(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(id, BookingOperations.Read, cancellationToken))
            return NotFound();

        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Forbidden();

        var views = await messages.ListForBookingAsync(scope, id, cancellationToken);
        return Ok(views.Select(ScheduledMessageDto.From).ToList());
    }

    /// <summary>
    /// Edits the text of one message for this booking only: <c>{ text }</c>, or an empty text to go back to the template of the routine. 404 for a message out of the reach; 409
    /// <c>scheduled_message_not_editable</c> for one that was sent, skipped, cancelled or expired, <c>scheduled_message_changed</c> when somebody else changed it meanwhile; 422 <c>message_text_invalid</c>,
    /// <c>template_variable_unknown</c>.
    /// </summary>
    [HttpPut("api/scheduled-messages/{id:guid}")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(ScheduledMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ScheduledMessageDto>> Edit(Guid id, [FromBody] EditScheduledMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Forbidden();
        if (!await CanWriteRowAsync(scope, id, cancellationToken) || User.GetUserId() is not { Length: > 0 } userId)
            return NotFound();

        return Ok(ScheduledMessageDto.From(await messages.EditAsync(scope, id, request.Text, userId, cancellationToken)));
    }

    /// <summary>
    /// «Skip»: the message will not be sent; the other people who reach the property are told. 404 for a message out of the reach; 409 <c>scheduled_message_not_editable</c> for one that already ended.
    /// </summary>
    [HttpPost("api/scheduled-messages/{id:guid}/skip")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(ScheduledMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ScheduledMessageDto>> Skip(Guid id, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Forbidden();
        if (!await CanWriteRowAsync(scope, id, cancellationToken) || User.GetUserId() is not { Length: > 0 } userId)
            return NotFound();

        return Ok(ScheduledMessageDto.From(await messages.SkipAsync(scope, id, userId, cancellationToken)));
    }

    /// <summary>
    /// «Send now»: sends the message at once, whatever its planned time and the send window, once whichever of the routine, the sweep and this call reaches it first. 200 with the message (a message the guest cannot be
    /// written to is answered as skipped by the system); 404 for a message out of the reach; 409 <c>scheduled_message_not_sendable</c> for one that was sent, skipped, cancelled or expired,
    /// <c>scheduled_message_in_progress</c> while another actor sends it; 422 <c>scheduled_message_blocked</c> with the missing items (<c>missing</c>) for one that is still blocked; 429.
    /// </summary>
    [HttpPost("api/scheduled-messages/{id:guid}/send-now")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [EnableRateLimiting(RateLimitPolicies.HostMessageSend)]
    [ProducesResponseType(typeof(ScheduledMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ScheduledMessageDto>> SendNow(Guid id, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Forbidden();
        if (!await CanWriteRowAsync(scope, id, cancellationToken) || User.GetUserId() is not { Length: > 0 } userId)
            return NotFound();

        return Ok(ScheduledMessageDto.From(await messages.SendNowAsync(scope, id, userId, cancellationToken)));
    }

    /// <summary>The row is in the reach of the caller (the service applies the scope in SQL) and the caller may write the booking of its property (the row check of the booking).</summary>
    private async Task<bool> CanWriteRowAsync(HostScope scope, Guid id, CancellationToken cancellationToken) =>
        await messages.FindBookingIdAsync(scope, id, cancellationToken) is { } bookingId
        && await IsAuthorizedAsync(bookingId, BookingOperations.Write, cancellationToken);

    private async Task<bool> IsAuthorizedAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation);
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private ObjectResult Forbidden() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}
