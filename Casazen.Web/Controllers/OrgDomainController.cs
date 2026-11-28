using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Owner-facing domain configuration for public booking hosts (#298 / US-024).
/// IDOR: route <c>orgId</c> must match the caller's resolved org.
/// </summary>
[ApiController]
[Route("api/orgs/{orgId:guid}/domain")]
[Authorize(Policy = "RequireOrgBillingAdmin")]
public class OrgDomainController(
    IOrgContextResolver orgContextResolver,
    IOrgDomainService orgDomainService,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    public const string SubdomainsNotConfiguredCode = "subdomains_not_configured";
    public const string DomainInvalidCode = "org_domain_invalid";
    public const string DomainInUseCode = "org_domain_in_use";
    public const string DomainNotConfiguredCode = "org_domain_not_configured";
    public const string DomainVerificationFailedCode = "org_domain_verification_failed";

    /// <summary>The owner sign-in is asked for a domain that is not verified (409 on the retry, 422 on the administrator's Ready), E40/F01/US02/T01.</summary>
    public const string DomainNotVerifiedCode = "org_domain_not_verified";

    [HttpGet]
    [ProducesResponseType(typeof(OrgDomainConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgDomainConfigDto>> GetDomain(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var config = await orgDomainService.GetDomainConfigAsync(orgId, cancellationToken);
        return config is null ? NotFound() : Ok(OrgDomainMapper.ToDto(config, localizer));
    }

    [HttpPost]
    [ProducesResponseType(typeof(OrgDomainConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgDomainConfigDto>> SetDomain(
        Guid orgId,
        [FromBody] SetOrgDomainRequest request,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var result = await orgDomainService.SetDomainAsync(
            orgId,
            request.HostMode,
            request.CustomDomain,
            request.Subdomain,
            cancellationToken);

        return this.DomainFailure(result) ?? Ok(OrgDomainMapper.ToDto(result.Config!, localizer));
    }

    [HttpPost("verify")]
    [ProducesResponseType(typeof(OrgDomainVerifyResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgDomainVerifyResultDto>> VerifyDomain(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var result = await orgDomainService.VerifyDomainAsync(orgId, cancellationToken);
        return this.DomainFailure(result) ?? Ok(OrgDomainMapper.ToDto(result.Verification!, localizer));
    }

    [HttpPut("surfaces")]
    [FeatureGate(FeatureFlags.WhiteLabelHost)]
    [ProducesResponseType(typeof(OrgDomainConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgDomainConfigDto>> SetSurfaces(
        Guid orgId,
        [FromBody] SetOrgDomainSurfacesRequest request,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var result = await orgDomainService.SetSurfacesAsync(
            orgId,
            new OrgDomainSurfaceSettings(
                request.GuestAreaOnOwnDomain!.Value,
                request.OwnerPortalOnOwnDomain!.Value,
                request.UseOwnDomainForLinks!.Value),
            cancellationToken);

        return this.DomainFailure(result) ?? Ok(OrgDomainMapper.ToDto(result.Config!, localizer));
    }

    /// <summary>
    /// The agency's «retry» of the owner sign-in on its verified domain (E40/F01/US02/T01, NF-141): with <c>Auth0HostSync</c> on the domain is registered in
    /// Auth0 again (idempotent; nothing is asked while it is ready already), with it off the request goes back to CasaZen (<c>Pending</c>). The answer is the
    /// whole domain configuration, <c>surfaces.ownerLoginHostState</c> updated. 404 while <c>WhiteLabelHost</c> is off, 403 for another org's id, 409
    /// <c>org_domain_not_verified</c> without a verified custom domain (whatever the plan), 403 <c>plan_required</c> for a verified domain the org no longer pays for, 429 past
    /// five calls in ten minutes for the same person (<see cref="RateLimitPolicies.OwnerLoginRetry"/>: the call reads and may write the Auth0 tenant that every host shares).
    /// </summary>
    [HttpPost("owner-login")]
    [FeatureGate(FeatureFlags.WhiteLabelHost)]
    [EnableRateLimiting(RateLimitPolicies.OwnerLoginRetry)]
    [ProducesResponseType(typeof(OrgDomainConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgDomainConfigDto>> RetryOwnerLogin(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        var result = await orgDomainService.RetryOwnerLoginAsync(orgId, cancellationToken);
        return result.Outcome switch
        {
            RetryOwnerLoginOutcome.Success when result.Config is not null => Ok(OrgDomainMapper.ToDto(result.Config, localizer)),
            RetryOwnerLoginOutcome.NotFound => NotFound(),
            RetryOwnerLoginOutcome.PlanRequired => this.PlanRequired(),
            _ => this.ApiProblem(StatusCodes.Status409Conflict, DomainNotVerifiedCode, "OrgDomainNotVerified"),
        };
    }

    [HttpPut("tracking")]
    [FeatureGate(FeatureFlags.SiteEmbed)]
    [ProducesResponseType(typeof(OrgDomainTrackingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgDomainTrackingDto>> SetTracking(
        Guid orgId,
        [FromBody] SetOrgDomainTrackingRequest request,
        CancellationToken cancellationToken)
    {
        var forbidden = await EnsureCallerOrgAsync(orgId, cancellationToken);
        if (forbidden is not null)
            return forbidden;

        if (!GtmContainerIds.TryNormalize(request.GtmContainerId, out _))
            return this.ApiProblem(StatusCodes.Status400BadRequest, GtmContainerIds.InvalidCode, "OrgGtmContainerIdInvalid");

        var result = await orgDomainService.SetTrackingAsync(orgId, request.GtmContainerId, cancellationToken);
        return result.Outcome switch
        {
            SetOrgDomainTrackingOutcome.Success when result.Tracking is not null =>
                Ok(new OrgDomainTrackingDto { GtmContainerId = result.Tracking.GtmContainerId }),
            SetOrgDomainTrackingOutcome.NotFound => NotFound(),
            _ => this.ApiProblem(StatusCodes.Status400BadRequest, GtmContainerIds.InvalidCode, "OrgGtmContainerIdInvalid"),
        };
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
