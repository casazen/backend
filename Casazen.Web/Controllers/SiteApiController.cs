using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The API of a connected plugin (<c>api/site/v1</c>, S022 and S023, site integration plan §10.3-§10.4, NF-204): the credential of the scheme <c>SiteToken</c>
/// only (<c>Authorization: Bearer czs_…</c>; a sign-in of a person is 401 here), policy <see cref="CasazenPolicies.SiteConnection"/>, behind <c>SiteEmbed</c>
/// (404 while it is off), 120 calls a minute per credential. The org is always the credential's (<c>User.GetSiteConnection()</c>), never in the address
/// or the body. S022 has the disconnection; the account and the domain of the plugin come with S023 (PLG-BE-SITEAPI).
/// </summary>
[ApiController]
[Route("api/site/v1")]
[Authorize(AuthenticationSchemes = SiteTokenDefaults.AuthenticationScheme, Policy = CasazenPolicies.SiteConnection)]
[FeatureGate(FeatureFlags.SiteEmbed)]
[EnableRateLimiting(RateLimitPolicies.SiteApi)]
[PrivateAnswer]
public class SiteApiController(ISiteConnectionService connections) : ControllerBase
{
    /// <summary>
    /// The plugin disconnects itself («Scollega», and at the uninstallation): its own credential stops working at once (the same credential is 401 next).
    /// 204.
    /// </summary>
    [HttpDelete("connection")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (User.GetSiteConnection() is not { } site)
            return Unauthorized();

        await connections.RevokeAsync(site.ConnectionId, cancellationToken);
        return NoContent();
    }
}
