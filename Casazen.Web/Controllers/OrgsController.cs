using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Org surfaces for the caller's organization (US-004): plan catalogue, entitlement and downgrades.
/// Paid upgrades go through Stripe Checkout (<see cref="BillingController"/>), never through this controller (#274).
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
    [Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntitlementDto>> GetMyEntitlement(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, cancellationToken);
        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId.Value, cancellationToken);

        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty,
            CanUseCustomDomain = canUseCustomDomain
        });
    }

    /// <summary>
    /// Moves the caller's org to a lower plan tier, or back to Starter (#274). Paid tiers are granted only by a
    /// Stripe subscription (checkout + webhooks): an upgrade without one is refused with 403
    /// <c>subscription_required</c>, and a plan driven by a live subscription is refused with 409
    /// <c>managed_by_stripe</c> (use the billing portal). Only the org's billing admin may call it.
    /// Downgrades are allowed even when usage exceeds the new limit; existing properties remain,
    /// but new creates are blocked until usage is under the limit.
    /// </summary>
    [HttpPut("me/plan")]
    [Authorize(Policy = "RequireOrgBillingAdmin")]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EntitlementDto>> UpdateMyPlan(
        [FromBody] UpdateOrgPlanDto dto,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryParseTier(dto.PlanTier, out var planTier))
            return BadRequest(new { error = $"Unknown planTier: {dto.PlanTier}" });

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await orgService.GetByIdAsync(orgId.Value, cancellationToken);
        if (org is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        switch (PlanChangePolicy.EvaluateManualChange(org, entitlementService.ResolveEffectiveTier(org), planTier))
        {
            case ManualPlanChangeOutcome.ManagedByStripe:
                return this.ApiProblem(StatusCodes.Status409Conflict, PlanProblemCodes.ManagedByStripe, "ManagedByStripe");
            case ManualPlanChangeOutcome.SubscriptionRequired:
                return this.ApiProblem(StatusCodes.Status403Forbidden, PlanProblemCodes.SubscriptionRequired, "SubscriptionRequired");
        }

        var updated = await orgService.UpdatePlanTierAsync(orgId.Value, planTier, cancellationToken);
        if (updated is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, cancellationToken);
        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId.Value, cancellationToken);
        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty,
            CanUseCustomDomain = canUseCustomDomain
        });
    }
}
