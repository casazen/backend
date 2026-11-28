using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.DTOs.SiteConnections;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The API of a connected plugin (<c>api/site/v1</c>, S022 and S023, site integration plan §10.3-§10.4, NF-202, NF-204): the credential of the scheme <c>SiteToken</c>
/// only (<c>Authorization: Bearer czs_…</c>; a sign-in of a person is 401 here), policy <see cref="CasazenPolicies.SiteConnection"/>, behind <c>SiteEmbed</c>
/// (404 while it is off), 120 calls a minute per credential. The org is always the credential's (<c>User.GetSiteConnection()</c>), never in the address or the body:
/// another org's id in a body is not read. S022 has the disconnection; S023 has the account and the domain: read it, set or remove the custom domain, check it, and
/// choose what passes from it. Every operation is one of the console's (<see cref="OrgDomainController"/>, <see cref="IOrgDomainService"/>): the same service, the
/// same mapping of the outcomes (<see cref="OrgDomainOutcomeResults"/>: 400 <c>org_domain_invalid</c>, 409 <c>org_domain_in_use</c>, 403 <c>plan_required</c>, ...) and
/// the same DTOs (<see cref="OrgDomainMapper"/>), so the plugin cannot read or do what the console would not.
/// </summary>
[ApiController]
[Route("api/site/v1")]
[Authorize(AuthenticationSchemes = SiteTokenDefaults.AuthenticationScheme, Policy = CasazenPolicies.SiteConnection)]
[FeatureGate(FeatureFlags.SiteEmbed)]
[EnableRateLimiting(RateLimitPolicies.SiteApi)]
[PrivateAnswer]
public class SiteApiController(
    ISiteConnectionService connections,
    ISiteAccountBuilder accounts,
    IOrgDomainService orgDomain,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary>
    /// Who the plugin is connected to and where the org is served: the org, <c>embedEnabled</c> (what <c>resolve-host</c> answers), <c>publicAddress</c> and
    /// <c>publicAddressKind</c> (<c>customDomain</c> for a verified domain the org pays for and is served on now, otherwise <c>casazen</c>) and <c>domain</c>, the
    /// object of <c>GET api/orgs/{orgId}/domain</c>. The commercial module of the console's domain endpoints is needed (403 <c>commercial_module_required</c>).
    /// </summary>
    [HttpGet("account")]
    [SiteDomainAccess]
    [ProducesResponseType(typeof(SiteAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SiteAccountDto>> GetAccount(CancellationToken cancellationToken)
    {
        if (User.GetSiteConnection() is not { } site)
            return Unauthorized();

        var (account, failure) = await BuildAccountAsync(site, domain: null, cancellationToken);
        return failure ?? Ok(account);
    }

    [HttpPut("domain")]
    [SiteDomainAccess]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(SiteAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SiteAccountDto>> SetDomain(
        [FromBody] SetSiteDomainRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetSiteConnection() is not { } site)
            return Unauthorized();

        if (!request.CustomDomainSpecified)
            return this.ApiProblem(StatusCodes.Status400BadRequest, OrgDomainController.DomainInvalidCode, "OrgDomainInvalid");

        var result = request.CustomDomain is null
            ? await orgDomain.SetDomainAsync(site.OrgId, PublicHostMode.CasazenPath, customDomain: null, subdomain: null, cancellationToken)
            : await orgDomain.SetDomainAsync(site.OrgId, PublicHostMode.CustomDomain, request.CustomDomain, subdomain: null, cancellationToken);
        if (this.DomainFailure(result) is { } refused)
            return refused;

        var (account, failure) = await BuildAccountAsync(site, result.Config, cancellationToken);
        return failure ?? Ok(account);
    }

    /// <summary>
    /// Checks the org's custom domain now (DNS, the ownership record and the Vercel project) and answers <c>{ verify, account }</c>: <c>verify</c> is the result as the
    /// console gets it, <c>account</c> the account as it is after the check. Same outcomes as the console: 400 <c>org_domain_not_configured</c> without a domain, 403
    /// <c>plan_required</c>. A budget of its own: 20 in ten minutes per credential.
    /// </summary>
    [HttpPost("domain/verify")]
    [EnableRateLimiting(RateLimitPolicies.SiteDomainVerify)]
    [SiteDomainAccess]
    [ProducesResponseType(typeof(SiteDomainVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SiteDomainVerifyResponse>> VerifyDomain(CancellationToken cancellationToken)
    {
        if (User.GetSiteConnection() is not { } site)
            return Unauthorized();

        var result = await orgDomain.VerifyDomainAsync(site.OrgId, cancellationToken);
        if (this.DomainFailure(result) is { } refused)
            return refused;

        var (account, failure) = await BuildAccountAsync(site, domain: null, cancellationToken);
        if (failure is not null)
            return failure;

        return Ok(new SiteDomainVerifyResponse(OrgDomainMapper.ToDto(result.Verification!, localizer), account!));
    }

    [HttpPut("domain/surfaces")]
    [FeatureGate(FeatureFlags.WhiteLabelHost)]
    [SiteDomainAccess]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(SiteAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SiteAccountDto>> SetSurfaces(
        [FromBody] SetOrgDomainSurfacesRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetSiteConnection() is not { } site)
            return Unauthorized();

        var result = await orgDomain.SetSurfacesAsync(
            site.OrgId,
            new OrgDomainSurfaceSettings(
                request.GuestAreaOnOwnDomain!.Value,
                request.OwnerPortalOnOwnDomain!.Value,
                request.UseOwnDomainForLinks!.Value),
            cancellationToken);
        if (this.DomainFailure(result) is { } refused)
            return refused;

        var (account, failure) = await BuildAccountAsync(site, result.Config, cancellationToken);
        return failure ?? Ok(account);
    }

    /// <summary>
    /// The plugin disconnects itself («Scollega», and at the uninstallation): its own credential stops working at once (the same credential is 401 next).
    /// 204. Never behind the commercial module: a lapsed module must not leave a credential that cannot be revoked.
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

    private async Task<(SiteAccountDto? Account, ActionResult? Failure)> BuildAccountAsync(
        SiteConnectionPrincipal site,
        OrgDomainConfig? domain,
        CancellationToken cancellationToken)
    {
        var built = await accounts.BuildAsync(site, domain, cancellationToken);
        if (built.Outcome == SiteAccountOutcome.Success && built.Account is not null)
            return (SiteAccountDto.From(built.Account, localizer), null);

        if (built.Outcome == SiteAccountOutcome.PublicAddressNotConfigured)
        {
            return (null, this.ApiProblem(
                StatusCodes.Status503ServiceUnavailable,
                SiteConnectionErrorCodes.PublicAddressNotConfigured,
                "SitePublicAddressNotConfigured"));
        }

        return (null, NotFound());
    }
}
