using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.SiteConnections;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The connected sites of the org, on the console (S022, PLG-BE-CONNECT, site integration plan §10.3, NF-201, NF-203): a billing administrator confirms the
/// connection of a site (<c>authorize</c>, the page <c>/app/connect/wordpress</c> of S025), lists the active ones and revokes one. Behind <c>SiteEmbed</c>
/// (404 while it is off), <c>RequireOrgBillingAdmin</c> and the IDOR check of <see cref="OrgDomainController"/>: the route <c>orgId</c> must be the caller's
/// org. Signed-in people only: a credential of a site (<c>czs_…</c>) is no sign-in here (401).
/// </summary>
[ApiController]
[Route("api/orgs/{orgId:guid}/site-connections")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
[FeatureGate(FeatureFlags.SiteEmbed)]
[PrivateAnswer]
public class SiteConnectionsController(
    IOrgContextResolver orgContextResolver,
    ISiteConnectionService connections) : ControllerBase
{
    public const int MaxAuthorizeRequestBytes = 8 * 1024;

    /// <summary>
    /// The billing administrator confirms that the site may connect to the org: a one-time code (five minutes) bound to the org, the account, the site and the
    /// PKCE challenge, and the address the browser goes back to on the site (<c>returnUrl</c> with <c>casazen_code</c> and <c>state</c> added, its query
    /// kept). 400 <c>site_connection_invalid_request</c> for any request that breaks a rule (the site an origin only, <c>https</c> but on a development host,
    /// the return address on the same origin, no credentials, no fragment, the state and the challenge well formed, <c>S256</c> only). 429 past ten in ten
    /// minutes for the same person.
    /// </summary>
    [HttpPost("authorize")]
    [EnableRateLimiting(RateLimitPolicies.SiteConnectAuthorize)]
    [RequestSizeLimit(MaxAuthorizeRequestBytes)]
    [ProducesResponseType(typeof(SiteConnectionAuthorizeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SiteConnectionAuthorizeResponse>> Authorize(
        Guid orgId,
        [FromBody] SiteConnectionAuthorizeRequest request,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var authorization = await connections.AuthorizeAsync(
            orgId,
            userId,
            request.SiteUrl,
            request.ReturnUrl,
            request.State,
            request.CodeChallenge,
            request.CodeChallengeMethod,
            cancellationToken);

        return authorization is null
            ? this.ApiProblem(StatusCodes.Status400BadRequest, SiteConnectionErrorCodes.InvalidRequest, "SiteConnectionInvalidRequest")
            : Ok(SiteConnectionAuthorizeResponse.From(authorization));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SiteConnectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SiteConnectionDto>>> List(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var active = await connections.ListAsync(orgId, cancellationToken);
        return Ok(active.Select(SiteConnectionDto.From).ToList());
    }

    [HttpDelete("{connectionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(
        Guid orgId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        return await connections.RevokeAsync(orgId, connectionId, cancellationToken)
            ? NoContent()
            : this.ApiProblem(StatusCodes.Status404NotFound, SiteConnectionErrorCodes.NotFound, "SiteConnectionNotFound");
    }

    private async Task<ActionResult?> EnsureCallerOrgAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var callerOrgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (callerOrgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        if (callerOrgId.Value != orgId)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "OrgAccessDenied");

        return null;
    }
}
