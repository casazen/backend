using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host's answer to "pay at the property" requests (decision D5, BK-06, A3-06): the list of the requests waiting for
/// an answer, accept (the booking becomes Confirmed and valid) and decline (cancelled, dates released). TN-3: the list
/// is filtered in SQL by the caller's scope (org, and owned properties for a non org-wide role); each answer needs
/// <c>booking.write</c> on the booking's property. A booking of another org, or one the caller may not write, answers 404.
/// </summary>
[ApiController]
[Route("api/bookings")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingApprovalController(
    IOnSiteBookingRequestService onSiteRequests,
    IBookingService bookingService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    ILogger<BookingApprovalController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Requests waiting for the host (guest email confirmed, deadline not passed), soonest deadline first.</summary>
    [HttpGet("approval-requests")]
    [ProducesResponseType(typeof(IReadOnlyList<BookingApprovalRequestDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BookingApprovalRequestDto>>> GetApprovalRequests(
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || User.GetHostScope(orgId.Value) is not { } scope)
            return Unauthorized();

        var requests = await onSiteRequests.GetAwaitingHostApprovalAsync(scope, cancellationToken);
        return Ok(requests.Select(BookingApprovalRequestDto.From).ToList());
    }

    /// <summary>
    /// Accepts the request: the booking becomes <c>Confirmed</c> (D5) and the guest gets an email. 409
    /// <c>onsite_request_not_pending</c> / <c>onsite_request_expired</c> when it was already answered or has expired (the
    /// second of two concurrent answers), <c>onsite_request_dates_blocked</c> when an OTA block imported meanwhile overlaps.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> Approve(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

        logger.LogInformation("Host accepting on-site request {BookingId}", id);
        await onSiteRequests.AcceptAsync(id, cancellationToken);
        return Ok(await ResponseOfAsync(id));
    }

    /// <summary>
    /// Declines the request: the booking is cancelled (<c>OnSiteRequestDeclined</c>), its dates released, and the guest
    /// gets an email with the optional <c>message</c>. Same 409 answers as <see cref="Approve"/>.
    /// </summary>
    [HttpPost("{id:guid}/decline")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> Decline(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeclineOnSiteRequestRequest? request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await CanWriteAsync(id, cancellationToken))
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

        logger.LogInformation("Host declining on-site request {BookingId}", id);
        await onSiteRequests.DeclineAsync(id, request?.Message, cancellationToken);
        return Ok(await ResponseOfAsync(id));
    }

    /// <summary>
    /// TN-3: the tenant filter hides a booking of another org; a visible one still needs <c>booking.write</c> on its
    /// property (owner or org-wide role).
    /// </summary>
    private async Task<bool> CanWriteAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return false;

        var property = await hostResources.ForPropertyAsync(booking.PropertyId, cancellationToken);
        if (property is null)
            return false;

        var resource = property with { OrgId = booking.OrgId };
        if (await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write))
            return true;

        logger.LogWarning("User {UserId} denied booking.write on booking {BookingId}", User.GetUserId(), bookingId);
        return false;
    }

    private async Task<BookingResponseDto?> ResponseOfAsync(Guid bookingId)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        return booking is null ? null : BookingMapper.ToResponse(booking, _clock.GetUtcNow().UtcDateTime);
    }
}
