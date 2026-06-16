using System.Security.Claims;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

public sealed class OrgBillingAdminRequirement : IAuthorizationRequirement;

public class OrgBillingAdminAuthorizationHandler(
    IOrgContextResolver orgContextResolver) : AuthorizationHandler<OrgBillingAdminRequirement>
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PropertyOwner",
        "PropertyManager",
        "Admin",
    };

    private static readonly HashSet<string> DeniedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Staff",
        "Guest",
    };

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OrgBillingAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        if (HasDeniedRole(context.User))
            return;

        if (!HasAllowedRole(context.User))
            return;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync();
        if (orgId is null)
            return;

        context.Succeed(requirement);
    }

    private static bool HasAllowedRole(ClaimsPrincipal user) =>
        user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "https://casazen.app/roles") &&
            AllowedRoles.Contains(c.Value));

    private static bool HasDeniedRole(ClaimsPrincipal user) =>
        user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "https://casazen.app/roles") &&
            DeniedRoles.Contains(c.Value));
}
