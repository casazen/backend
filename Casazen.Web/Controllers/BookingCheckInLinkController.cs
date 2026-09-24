using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guest check-in link of a booking, host side (US-020, CO-09, A5-26): state of the current link and of its email, a
/// new link to copy, a new link sent by email. The link is always returned to the host, whatever happens to the email,
/// so a wrong address or a provider error never leaves the host without a way to reach the guest. Only the hash of a
/// token is stored: a link can be copied when it is issued, and a new one replaces the previous one.
/// </summary>
/// <remarks>
/// TN-3: a booking of another org is invisible (404); on a visible one the caller needs <c>booking.read</c> or
/// <c>booking.write</c> on its property (otherwise 403). Moved from <c>BookingsController</c>, same routes.
/// </remarks>
[ApiController]
[Route("api/bookings")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingCheckInLinkController(
    IBookingService bookingService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IGuestCheckInService checkInService,
    IGuestCheckInLinkEmailQueue linkEmails,
    PublicSiteLinks publicSiteLinks,
    ILogger<BookingCheckInLinkController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    /// <summary>409: a link exists only for confirmed or checked-in bookings.</summary>
    public const string BookingNotEligibleCode = "checkin_link_booking_not_eligible";

    /// <summary>409: the guest has already completed the check-in (the host corrects the data in the Alloggiati tab).</summary>
    public const string AlreadyCompletedCode = "checkin_already_completed";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The current check-in link of the booking and the state of its email.</summary>
    [HttpGet("{id:guid}/checkin-session")]
    [ProducesResponseType(typeof(CheckInSessionStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckInSessionStatusResponse>> GetCheckInSession(Guid id)
    {
        var (booking, denied) = await AuthorizeBookingAsync(id, BookingOperations.Read);
        if (denied is not null)
            return denied;

        var session = await checkInService.GetSessionForBookingAsync(id);
        return Ok(CheckInSessionStatusResponse.From(session, IsEligible(booking!.Status), _clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Generates a new link to copy and send to the guest another way (no email). The previous link stops working.
    /// </summary>
    [HttpPost("{id:guid}/checkin/link")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(CheckInLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CheckInLinkResponse>> CreateCheckInLink(Guid id) => IssueAsync(id, sendEmail: false);

    /// <summary>
    /// Generates a new link and queues its email to the guest (also the reminder). The link is returned whatever happens
    /// to the email, with its real state: <c>Queued</c>, or <c>Failed</c> with the reason (no address, no provider).
    /// The previous link stops working.
    /// </summary>
    [HttpPost("{id:guid}/checkin/resend-link")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(CheckInLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CheckInLinkResponse>> ResendCheckInLink(Guid id) => IssueAsync(id, sendEmail: true);

    private async Task<ActionResult<CheckInLinkResponse>> IssueAsync(Guid id, bool sendEmail)
    {
        var (booking, denied) = await AuthorizeBookingAsync(id, BookingOperations.Write);
        if (denied is not null)
            return denied;

        if (!IsEligible(booking!.Status))
            return this.ApiProblem(StatusCodes.Status409Conflict, BookingNotEligibleCode, "CheckInLinkBookingNotEligible");

        var current = await checkInService.GetSessionForBookingAsync(id);
        if (current?.IsCompleted == true)
            return this.ApiProblem(StatusCodes.Status409Conflict, AlreadyCompletedCode, "CheckInAlreadyCompleted");

        // A missing App:PublicSiteBaseUrl is a configuration error (500) before any link is issued: never a wrong link.
        publicSiteLinks.EnsureConfigured();

        var link = await checkInService.IssueLinkAsync(booking.Id, booking.OrgId);
        var email = sendEmail
            ? await linkEmails.QueueAsync(link.SessionId, link.Token, HttpContext.RequestAborted)
            : new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.NotRequested);

        logger.LogInformation(
            "Check-in link of booking {BookingId} issued by user {UserId}, email {EmailStatus}",
            id, User.GetUserId(), email.Status);

        return Ok(new CheckInLinkResponse
        {
            CheckInLink = publicSiteLinks.GuestCheckIn(link.Token),
            ExpiresAt = link.ExpiresAt,
            EmailStatus = email.Status,
            EmailError = email.Error,
        });
    }

    /// <summary>
    /// TN-3: the booking must be visible (tenant filter, otherwise 404) and the caller must hold
    /// <paramref name="operation"/> on its property (otherwise 403).
    /// </summary>
    private async Task<(Booking? Booking, ActionResult? Denied)> AuthorizeBookingAsync(
        Guid bookingId,
        HostOperationRequirement operation)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return (null, BookingNotFound());

        var property = await hostResources.ForPropertyAsync(booking.PropertyId, HttpContext.RequestAborted);
        if (property is null)
            return (null, BookingNotFound());

        if (!await authorizationService.IsAuthorizedAsync(User, property with { OrgId = booking.OrgId }, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the check-in link of booking {BookingId}",
                User.GetUserId(), operation.PermissionKey, bookingId);
            return (null, Forbid());
        }

        return (booking, null);
    }

    private static bool IsEligible(BookingStatus status) =>
        status is BookingStatus.Confirmed or BookingStatus.CheckedIn;

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");
}
