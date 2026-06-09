using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Orgs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Read-only org surfaces for the caller's own organization (US-004). Org management and plan
/// switching are owned by <c>spec-saas-billing</c>; this controller only exposes entitlement.
/// </summary>
[ApiController]
[Route("api/orgs")]
[Authorize]
public class OrgsController(
    ITenantContext tenantContext,
    IEntitlementService entitlementService) : ControllerBase
{
    /// <summary>
    /// Returns the caller org's plan entitlement: tier, limits, current usage, and whether
    /// another property may be created (AC8). The <c>OrgId</c> is resolved from the authenticated
    /// principal via <see cref="ITenantContext"/> and is never accepted from the client.
    /// </summary>
    /// <response code="200">Entitlement for the caller's org.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="404">The caller is not assigned to an org yet.</response>
    [HttpGet("me/entitlement")]
    [Authorize(Policy = "RequireContext:short-rent:property.read")]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntitlementDto>> GetMyEntitlement(CancellationToken cancellationToken)
    {
        var orgId = tenantContext.OrgId;
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
}
