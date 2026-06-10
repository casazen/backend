using Casazen.Core.Services;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Org surfaces for the caller's organization (US-004). MVP plan catalogue and tier changes
/// live here until <c>spec-saas-billing</c> adds Stripe Checkout for paid upgrades.
/// </summary>
[ApiController]
[Route("api/orgs")]
public class OrgsController(
    IOrgContextResolver orgContextResolver,
    IEntitlementService entitlementService,
    IOrgService orgService) : ControllerBase
{
    /// <summary>Returns available plan tiers and property limits.</summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<PlanDto>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<PlanDto>> GetPlans() =>
        Ok(PlanCatalog.All.Select(p => new PlanDto
        {
            Tier = p.Tier.ToString(),
            DisplayName = p.DisplayName,
            MaxProperties = p.MaxProperties == int.MaxValue ? -1 : p.MaxProperties,
            Description = p.Description,
        }));

    /// <summary>
    /// Returns the caller org's plan entitlement: tier, limits, current usage, and whether
    /// another property may be created (AC8).
    /// </summary>
    [HttpGet("me/entitlement")]
    [Authorize(Policy = "RequireContext:short-rent:property.read")]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntitlementDto>> GetMyEntitlement(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, cancellationToken);

        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty
        });
    }

    /// <summary>
    /// Updates the caller's org plan tier (MVP self-serve change before Stripe billing).
    /// Downgrades are allowed even when usage exceeds the new limit; existing properties remain,
    /// but new creates are blocked until usage is under the limit.
    /// </summary>
    [HttpPut("me/plan")]
    [Authorize]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntitlementDto>> UpdateMyPlan(
        [FromBody] UpdateOrgPlanDto dto,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryParseTier(dto.PlanTier, out var planTier))
            return BadRequest(new { error = $"Unknown planTier: {dto.PlanTier}" });

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var updated = await orgService.UpdatePlanTierAsync(orgId.Value, planTier, cancellationToken);
        if (updated is null)
            return NotFound(new { error = "Organization not found" });

        var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, cancellationToken);
        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty
        });
    }
}
