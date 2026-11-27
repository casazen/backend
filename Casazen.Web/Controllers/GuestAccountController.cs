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
        var (email, verified) = await accountEmails.ResolveAsync(User, User.GetUserId()!, cancellationToken);
        if (!verified || string.IsNullOrWhiteSpace(email))
            throw new DomainForbiddenException(GuestAccountErrorCodes.EmailNotVerified, "GuestEmailNotVerified");

        return email.Trim();
    }
}
