using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Web.DTOs.SiteConnections;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The exchange of the one-time code of a site for the credential of its plugin (S022, PLG-BE-CONNECT, site integration plan §10.3, NF-201, NF-204):
/// anonymous, called by the server of WordPress, the code and the verifier in the body (never in an address). Behind <c>SiteEmbed</c> (404 while it is off)
/// and rate limited per client IP.
/// </summary>
[ApiController]
[Route("api/public/site-connections")]
[AllowAnonymous]
[FeatureGate(FeatureFlags.SiteEmbed)]
[PrivateAnswer]
public class PublicSiteConnectionsController(ISiteConnectionService connections) : ControllerBase
{
    public const int MaxTokenRequestBytes = 4 * 1024;

    /// <summary>
    /// The right code, its verifier (PKCE <c>S256</c>) and the site it was given for: the credential (<c>czs_…</c>, shown this once), the connection and the
    /// org; an earlier connection of the same site stops working. Every other case (unknown, expired, used, wrong verifier, another site, malformed) is the
    /// same 400 <c>site_connection_invalid_grant</c>, with no detail. 429 past twenty in ten minutes from one address.
    /// </summary>
    [HttpPost("token")]
    [EnableRateLimiting(RateLimitPolicies.SiteConnectToken)]
    [RequestSizeLimit(MaxTokenRequestBytes)]
    [ProducesResponseType(typeof(SiteConnectionTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SiteConnectionTokenResponse>> Token(
        [FromBody] SiteConnectionTokenRequest request,
        CancellationToken cancellationToken)
    {
        var grant = await connections.ExchangeAsync(request.Code, request.CodeVerifier, request.SiteUrl, cancellationToken);
        return grant is null
            ? this.ApiProblem(StatusCodes.Status400BadRequest, SiteConnectionErrorCodes.InvalidGrant, "SiteConnectionInvalidGrant")
            : Ok(SiteConnectionTokenResponse.From(grant));
    }
}
