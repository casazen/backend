using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.HostInvites;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The two calls of the person who received a host invite: what the link is for, and the acceptance. The invite grants no role:
/// accepting only marks it used and records who invited whom. Never cached.
/// </summary>
[ApiController]
[Route("api/host-invites")]
public class HostInviteLinksController(
    IHostInviteService invites,
    IAccountEmailResolver accountEmails,
    ILogger<HostInviteLinksController> logger) : ControllerBase
{
    /// <summary>
    /// What an invite link is for, to show on the public page <c>/invito-host/{token}</c>. Anonymous and rate limited. Always 200:
    /// <c>valid</c> is true only for a pending invite, which also gets <c>inviterName</c> and <c>maskedEmail</c> (the local part is
    /// hidden: never the whole email); malformed, unknown, used, expired and revoked links get <c>valid</c> false and nothing else.
    /// The token is in the query string because the page's contract says so, which puts it in access logs: prefer
    /// <c>POST api/host-invites/lookup</c>.
    /// </summary>
    [HttpGet("lookup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicInvitationLookup)]
    [ProducesResponseType(typeof(HostInviteLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<HostInviteLookupResponse>> Lookup([FromQuery] string? token, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(HostInviteLookupResponse.From(await invites.LookupAsync(token, cancellationToken)));
    }

    /// <summary>The same as the GET lookup with the token in the body, so it never ends up in a URL or a request log.</summary>
    [HttpPost("lookup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicInvitationLookup)]
    [ProducesResponseType(typeof(HostInviteLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<HostInviteLookupResponse>> LookupByBody(
        [FromBody] HostInviteLookupRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(HostInviteLookupResponse.From(await invites.LookupAsync(request.Token, cancellationToken)));
    }

    /// <summary>
    /// The signed-in account takes the invite. Its <b>verified</b> email (from the token or Auth0, never from the body) must be the
    /// invited one. 204; sending it again by the same account is 204 too. 422 <c>host_invite_invalid</c>, <c>host_invite_expired</c>,
    /// <c>host_invite_revoked</c>, <c>host_invite_email_not_verified</c>, <c>host_invite_email_mismatch</c>; 409 <c>host_invite_used</c>
    /// (taken by another account).
    /// </summary>
    [HttpPost("accept")]
    [Authorize(Policy = CasazenPolicies.Authenticated)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Accept([FromBody] AcceptHostInviteRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var (email, emailVerified) = await accountEmails.ResolveAsync(User, userId, cancellationToken);
        logger.LogInformation("Host invite acceptance attempted by user {UserId}, email verified {EmailVerified}", userId, emailVerified);

        await invites.AcceptAsync(new AcceptHostInvite(request.Token, userId, email, emailVerified), cancellationToken);
        return NoContent();
    }
}
