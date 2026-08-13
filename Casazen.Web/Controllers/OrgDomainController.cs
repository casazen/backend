using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    IOrgDomainService orgDomainService) : ControllerBase
{
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
        return config is null ? NotFound() : Ok(Map(config));
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

        return result.Outcome switch
        {
            SetOrgDomainOutcome.Success when result.Config is not null => Ok(Map(result.Config)),
            SetOrgDomainOutcome.NotFound => NotFound(),
            SetOrgDomainOutcome.PlanRequired => StatusCode(
                StatusCodes.Status403Forbidden,
                new { code = "plan_required", requiredPlan = "Pro" }),
            SetOrgDomainOutcome.Conflict => Conflict(new { error = "Domain or subdomain already in use" }),
            SetOrgDomainOutcome.ValidationError => BadRequest(new { error = result.ErrorMessage }),
            _ => BadRequest(new { error = result.ErrorMessage ?? "Invalid domain configuration" }),
        };
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
        return result.Outcome switch
        {
            VerifyOrgDomainOutcome.Success when result.Verification is not null => Ok(new OrgDomainVerifyResultDto
            {
                DomainVerificationStatus = result.Verification.Status,
                CustomDomain = result.Verification.CustomDomain,
                CheckedAt = result.Verification.CheckedAt,
                Message = result.Verification.Message,
            }),
            VerifyOrgDomainOutcome.NotFound => NotFound(),
            VerifyOrgDomainOutcome.NotConfigured => BadRequest(new
            {
                error = "Custom domain is not configured for this organization",
            }),
            _ => BadRequest(new { error = "Domain verification failed" }),
        };
    }

    private async Task<ActionResult?> EnsureCallerOrgAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var callerOrgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (callerOrgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        if (callerOrgId.Value != orgId)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Access denied for this organization" });

        return null;
    }

    private static OrgDomainConfigDto Map(OrgDomainConfig config) => new()
    {
        OrgId = config.OrgId,
        PublicHostMode = config.PublicHostMode,
        Subdomain = config.Subdomain,
        CustomDomain = config.CustomDomain,
        DomainVerificationStatus = config.DomainVerificationStatus,
        CanUseCustomDomain = config.CanUseCustomDomain,
        DnsInstructions = config.DnsInstructions is null
            ? null
            : new DnsInstructionsDto
            {
                CnameHost = config.DnsInstructions.CnameHost,
                CnameTarget = config.DnsInstructions.CnameTarget,
                TxtHost = config.DnsInstructions.TxtHost,
                TxtValue = config.DnsInstructions.TxtValue,
                SslNote = config.DnsInstructions.SslNote,
            },
        PublicUrls = new PublicUrlsDto
        {
            PathUrl = config.PublicUrls.PathUrl,
            SubdomainUrl = config.PublicUrls.SubdomainUrl,
            CustomDomainUrl = config.PublicUrls.CustomDomainUrl,
        },
    };
}
