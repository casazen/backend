using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

public sealed class AgencyTierRequirement : IAuthorizationRequirement;

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

        if (org.PlanTier != PlanTier.Agency)
        {
            context.Fail(new AuthorizationFailureReason(this, "feature_requires_agency_tier"));
            return;
        }

        if (org.IsAgencyTierDowngraded)
        {
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

        context.Succeed(requirement);
    }
}
