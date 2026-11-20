using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The stay link of a booking, host side (E50/F01/US01/T01, NF-152, D1, D2): read it, get it (made on request the first time), regenerate it and revoke
/// it. The link is the address <c>/s/{token}</c> of the guest portal of the stay, for a booking of any source; the host copies it from the booking and
/// pastes it where the guest reads (the chat of a portal, a message). Behind <c>Features:GuestPortal</c>: off, every endpoint answers 404 before
/// authentication.
/// </summary>
/// <remarks>
/// TN-3, like <see cref="BookingCheckInLinkController"/>: a booking of another org is invisible (404); on a visible one the caller needs
/// <c>booking.read</c> to read and <c>booking.write</c> to change, on its property (otherwise 403; a collaborator «Solo alcuni» reaches the properties it
/// was given only). The address itself opens the guest portal, so it is returned only to a caller who may write the booking, and every answer is
/// private (<c>Cache-Control: no-store</c>). The logs carry ids, never the token.
/// </remarks>
[ApiController]
[Route("api/bookings")]
[FeatureGate(FeatureFlags.GuestPortal)]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class GuestPortalLinkController(
    IBookingService bookingService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IGuestPortalLinkService links,
    PublicSiteLinks publicSiteLinks,
    ILogger<GuestPortalLinkController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The stay link of the booking: <c>{ state, url, createdAt, expiresAt, eligible, ineligibleReason }</c>. Never makes one. <c>url</c> is there only
    /// while the link is active and only for a caller who may write the booking.
    /// </summary>
    [HttpGet("{bookingId:guid}/guest-portal")]
    [ProducesResponseType(typeof(GuestPortalLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GuestPortalLinkDto>> Get(Guid bookingId, CancellationToken cancellationToken)
    {
        var (booking, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Read, cancellationToken);
        if (booking is null)
            return denied!;

        var link = await links.GetActiveAsync(booking.OrgId, bookingId, cancellationToken);
        var canCopy = await CanAsync(booking, BookingOperations.Write, cancellationToken);
        return Ok(GuestPortalLinkDto.From(link, booking, Now(), l => canCopy ? links.BuildUrl(l) : null));
    }

    /// <summary>
    /// The stay link of the booking, made now if it has none: the call is safe to repeat and never changes the address of a link that exists
    /// (<c>regenerate</c> does). 409 <c>guest_portal_booking_not_eligible</c> for a request, a cancelled booking, a no-show or a stay over by more
    /// than 30 days.
    /// </summary>
    [HttpPost("{bookingId:guid}/guest-portal")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(GuestPortalLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GuestPortalLinkDto>> GetOrCreate(Guid bookingId, CancellationToken cancellationToken)
    {
        var (booking, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Write, cancellationToken);
        if (booking is null)
            return denied!;

        if (GuestPortalRules.IneligibleReason(booking, Now()) is { } reason)
            return NotEligible(reason);

        // A missing App:PublicSiteBaseUrl is a configuration error (500) before any link is made: never a wrong address.
        publicSiteLinks.EnsureConfigured();
        var result = await links.GetOrCreateAsync(booking.OrgId, bookingId, User.GetUserId(), cancellationToken);
        if (result is null)
            return NotEligible(GuestPortalRules.IneligibleReason(booking, Now()) ?? GuestPortalRules.BookingStatusReason);

        if (result.Created)
            logger.LogInformation("Stay link {LinkId} of booking {BookingId} made by user {UserId}", result.Link.Id, bookingId, User.GetUserId());

        return Ok(GuestPortalLinkDto.From(result.Link, booking, Now(), links.BuildUrl));
    }

    /// <summary>
    /// A new address: the active link is revoked and a new one is made, in one step. The old address stops opening at once (the portal answers it like
    /// one that never existed). 409 <c>guest_portal_booking_not_eligible</c> as for the first link.
    /// </summary>
    [HttpPost("{bookingId:guid}/guest-portal/regenerate")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(GuestPortalLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GuestPortalLinkDto>> Regenerate(Guid bookingId, CancellationToken cancellationToken)
    {
        var (booking, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Write, cancellationToken);
        if (booking is null)
            return denied!;

        if (GuestPortalRules.IneligibleReason(booking, Now()) is { } reason)
            return NotEligible(reason);

        publicSiteLinks.EnsureConfigured();
        var link = await links.RegenerateAsync(booking.OrgId, bookingId, User.GetUserId(), cancellationToken);
        if (link is null)
            return NotEligible(GuestPortalRules.IneligibleReason(booking, Now()) ?? GuestPortalRules.BookingStatusReason);

        logger.LogInformation("Stay link of booking {BookingId} regenerated by user {UserId}: {LinkId} is the active one", bookingId, User.GetUserId(), link.Id);
        return Ok(GuestPortalLinkDto.From(link, booking, Now(), links.BuildUrl));
    }

    /// <summary>Revokes the stay link of the booking: its address stops opening at once. 204, also when the booking had none.</summary>
    [HttpDelete("{bookingId:guid}/guest-portal")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid bookingId, CancellationToken cancellationToken)
    {
        var (booking, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Write, cancellationToken);
        if (booking is null)
            return denied!;

        if (await links.RevokeAsync(booking.OrgId, bookingId, cancellationToken))
            logger.LogInformation("Stay link of booking {BookingId} revoked by user {UserId}", bookingId, User.GetUserId());

        return NoContent();
    }

    /// <summary>
    /// TN-3: the booking must be visible (tenant filter, otherwise 404) and the caller must hold <paramref name="operation"/> on its property (otherwise
    /// 403).
    /// </summary>
    private async Task<(Booking? Booking, ActionResult? Denied)> AuthorizeBookingAsync(
        Guid bookingId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return (null, BookingNotFound());

        var property = await hostResources.ForPropertyAsync(booking.PropertyId, cancellationToken);
        if (property is null)
            return (null, BookingNotFound());

        if (!await authorizationService.IsAuthorizedAsync(User, property with { OrgId = booking.OrgId }, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the stay link of booking {BookingId}",
                User.GetUserId(), operation.PermissionKey, bookingId);
            return (null, Forbid());
        }

        return (booking, null);
    }

    /// <summary>True when the caller holds <paramref name="operation"/> on the property of the booking (the booking is already known to be visible).</summary>
    private async Task<bool> CanAsync(Booking booking, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(booking.PropertyId, cancellationToken);
        return property is not null
               && await authorizationService.IsAuthorizedAsync(User, property with { OrgId = booking.OrgId }, operation);
    }

    private ObjectResult NotEligible(string reason) =>
        this.ApiProblem(
            StatusCodes.Status409Conflict,
            GuestPortalRules.BookingNotEligibleCode,
            reason == GuestPortalRules.StayEndedReason ? "GuestPortalStayEnded" : "GuestPortalBookingNotEligible");

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
