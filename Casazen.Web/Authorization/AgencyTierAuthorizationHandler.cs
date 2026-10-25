using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

public sealed class AgencyTierRequirement : IAuthorizationRequirement;

/// <summary>
/// Agency tier authorization (ADR-010-R2, ADR-010-R5): all <c>agency.*</c> policies require
/// <c>Org.PlanTier == PlanTier.Agency</c>. Returns 403 <c>feature_requires_agency_tier</c> if not.
/// When tier is Agency but <see cref="Casazen.Core.Entities.Org.IsAgencyTierDowngraded"/> is true,
/// mutations (POST/PUT/PATCH/DELETE) return 403 <c>tier_insufficient</c> while reads (GET) succeed.
/// </summary>
public class AgencyTierAuthorizationHandler(
    IOrgContextResolver orgContextResolver,
    IOrgService orgService,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<AgencyTierRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AgencyTierRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync();
        if (orgId is null)
            return;

        var org = await orgService.GetByIdAsync(orgId.Value);
        if (org is null)
            return;

        // Non-agency org: deny with feature_requires_agency_tier
        if (org.PlanTier != PlanTier.Agency)
        {
            context.Fail(new AuthorizationFailureReason(this, "feature_requires_agency_tier"));
            return;
        }

        // Agency tier, but check if downgraded
        if (org.IsAgencyTierDowngraded)
        {
            // Downgraded agency: mutations are denied, reads are allowed
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext is not null)
            {
                var method = httpContext.Request.Method;
                var isMutation = method is "POST" or "PUT" or "PATCH" or "DELETE";

                if (isMutation)
                {
                    context.Fail(new AuthorizationFailureReason(this, "tier_insufficient"));
                    return;
                }
            }
        }

        // Agency tier and not downgraded, or downgraded but read operation
        context.Succeed(requirement);
    }
}
