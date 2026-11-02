using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guest who has a CasaZen account (the mobile app, store rule: the guest searches for free, registers to book, sees «Le mie
/// prenotazioni» and can delete the account from the app). Behind <see cref="CasazenPolicies.GuestAccount"/>: any signed-in account, with no
/// host org and no supplier link needed. Never indexable and never cached. Runbook <c>docs/runbooks/direct-booking.md</c> § 8.6.
/// </summary>
/// <remarks>
/// <para><b>What ties a booking to the account is its email.</b> The bookings were made on a public site with no account, so they carry
/// the address typed at the checkout; the account's address, <b>verified</b> by Auth0, is the only link. The three booking actions answer
/// 403 <c>guest_email_not_verified</c> while it is not (the claim is read from the access token: after verifying the address the app needs a
/// new token). The deletion does not need it.</para>
/// <para><b>One 404.</b> A booking that is not the account's, one that does not exist and an id that is not an id get the same
/// <c>guest_booking_not_found</c>.</para>
/// </remarks>
[ApiController]
[Route("api/guest")]
[Authorize(Policy = CasazenPolicies.GuestAccount)]
[PrivateAnswer]
public class GuestAccountController(
    IGuestAccountBookingService bookings,
    IUserService users,
    IAccountEmailResolver accountEmails) : ControllerBase
{
    /// <summary>
    /// The account's direct bookings of every booking site. <c>scope</c> is <c>upcoming</c> (check-out today or later in Europe/Rome, by
    /// check-in ascending; the default) or <c>past</c> (by check-in descending); <c>page</c> from 1, <c>pageSize</c> up to 50 (20 by
    /// default). 200 <c>{ items, total, page, pageSize }</c>; 400 <c>validation_error</c> for another scope; 401; 403
    /// <c>guest_email_not_verified</c>; 429. The lapsed holds and the payments that came when the dates were gone are not listed.
    /// </summary>
    [HttpGet("bookings")]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountRead)]
    [ProducesResponseType(typeof(GuestAccountBookingPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestAccountBookingPageResponse>> ListBookings(
        [FromQuery] string? scope,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = GuestAccountBookings.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var email = await VerifiedEmailAsync(cancellationToken);

        var parsedScope = GuestAccountBookingScope.Upcoming;
        if (!string.IsNullOrWhiteSpace(scope) && !EnumNames.TryParseDefined(scope, out parsedScope))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "GuestAccountScopeUnknown");

        var result = await bookings.ListAsync(email, parsedScope, page, pageSize, cancellationToken);
        return Ok(GuestAccountBookingPageResponse.From(result));
    }

    /// <summary>
    /// One booking of the account, as «Le mie prenotazioni» shows it (the body of <c>GET api/public/guest/bookings/{code}</c>) plus
    /// <c>id</c> and <c>orgSlug</c>. 401; 403 <c>guest_email_not_verified</c>; <b>404 <c>guest_booking_not_found</c></b> for a booking that is
    /// not the account's, whatever the reason; 429.
    /// </summary>
    [HttpGet("bookings/{id}")]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountRead)]
    [ProducesResponseType(typeof(GuestAccountBookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestAccountBookingDetailResponse>> GetBooking(string id, CancellationToken cancellationToken)
    {
        var email = await VerifiedEmailAsync(cancellationToken);
        var bookingId = Guid.TryParse(id, out var parsed) ? parsed : throw GuestAccountBookings.NotFound();

        return Ok(GuestAccountBookingDetailResponse.From(await bookings.GetAsync(email, bookingId, cancellationToken)));
    }

    /// <summary>
    /// Opens the guest-area session of the booking's site for the account's email: <c>{ accessToken, expiresAt, bookingCode, orgSlug }</c>.
    /// The token is the one a booking code and an email open (<c>gs1.</c>, 2 hours by default, 24 at most, no refresh): the app sends it as
    /// <c>Authorization: Bearer</c> to <c>api/public/guest/bookings/{bookingCode}/…</c> (the cancellation with its preview, the messages with
    /// the host) and asks for a new one when it ends. 401; 403 <c>guest_email_not_verified</c>; 404 <c>guest_booking_not_found</c>; 429.
    /// </summary>
    [HttpPost("bookings/{id}/session")]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountSession)]
    [ProducesResponseType(typeof(GuestAccountSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestAccountSessionResponse>> OpenSession(string id, CancellationToken cancellationToken)
    {
        var email = await VerifiedEmailAsync(cancellationToken);
        var bookingId = Guid.TryParse(id, out var parsed) ? parsed : throw GuestAccountBookings.NotFound();

        return Ok(GuestAccountSessionResponse.From(await bookings.OpenSessionAsync(email, bookingId, cancellationToken)));
    }

    /// <summary>
    /// Deletes the caller's own account (the store rule «delete account in the app»). 204 when it is gone. Auth0 is deleted first: when it
    /// refuses the answer is 502 with its <c>auth0_*</c> code and nothing is removed, so the app can ask again. 409
    /// <c>guest_account_is_professional</c> for an account that also has a host org, a supplier link or an administrator's role (support
    /// removes those). The bookings and the guest records of the hosts are <b>not</b> touched: they are the hosts' books, tied to the email
    /// typed at the checkout and not to the account. The access token already issued works until it expires, but the account is gone: a
    /// second call answers 401. Needs no verified email. 429.
    /// </summary>
    [HttpDelete("account")]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DeleteAccount(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // The token of an account that was already deleted still verifies until it expires (it is a signed JWT), but the account is gone.
        if (await users.GetUserAsync(userId) is null)
            return Unauthorized();

        var sync = await users.DeleteOwnGuestAccountAsync(userId, cancellationToken);
        if (!sync.Succeeded)
        {
            return this.ApiProblem(
                StatusCodes.Status502BadGateway,
                sync.ErrorCode ?? Auth0SyncResult.ApiErrorCode,
                "GuestAccountDeleteFailed");
        }

        return NoContent();
    }

    /// <summary>
    /// The email of the signed-in account when Auth0 verified it (the Action claim, else the Management API:
    /// <see cref="IAccountEmailResolver"/>); otherwise 403 <c>guest_email_not_verified</c>.
    /// </summary>
    private async Task<string> VerifiedEmailAsync(CancellationToken cancellationToken)
    {
        // The policy asks for a subject, so there is one.
        var (email, verified) = await accountEmails.ResolveAsync(User, User.GetUserId()!, cancellationToken);
        if (!verified || string.IsNullOrWhiteSpace(email))
            throw new DomainForbiddenException(GuestAccountErrorCodes.EmailNotVerified, "GuestEmailNotVerified");

        return email.Trim();
    }
}
