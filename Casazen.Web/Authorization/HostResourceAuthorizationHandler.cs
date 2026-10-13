using Casazen.Core.Authorization;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

/// <summary>
/// Resource-based authorization of host rows (TN-3). An operation on a <see cref="HostResource"/> succeeds only when
/// <list type="number">
/// <item>the caller's org (the same <see cref="ITenantContext"/> the EF tenant filter uses) is the row's org;</item>
/// <item>the caller holds the operation's permission in one of its contexts (DB membership or JWT fallback);</item>
/// <item>for rows bound to a property, the caller reaches it (<see cref="IHostScopeResolver"/>, AM-03): its org role says every
/// property of the org, or it was given this one («Solo alcuni»); an account in no org team reaches the ones it created, or all
/// of them with an org-wide token role (<see cref="HostRoles.OrgWide"/>).</item>
/// </list>
/// Everything else fails closed: no user id, no org, another org, missing permission.
/// </summary>
/// <remarks>
/// Usage: <c>await authorizationService.AuthorizeAsync(User, HostResource.ForProperty(property), PropertyOperations.Write)</c>.
/// Controllers answer 404 when the row is not visible (other org) and 403 when it is visible but not allowed.
/// </remarks>
public sealed class HostResourceAuthorizationHandler(
    ITenantContext tenantContext,
    IContextAuthorizationService contextAuthorizationService,
    IHostScopeResolver hostScopeResolver,
    ILogger<HostResourceAuthorizationHandler> logger)
    : AuthorizationHandler<HostOperationRequirement, HostResource>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HostOperationRequirement requirement,
        HostResource resource)
    {
        var userId = context.User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return;

        if (tenantContext.OrgId is not Guid callerOrgId || callerOrgId != resource.OrgId)
        {
            logger.LogDebug(
                "Host resource denied: user {UserId} is not in org {OrgId} ({Permission})",
                userId, resource.OrgId, requirement.PermissionKey);
            return;
        }

        if (resource.IsBoundToProperty && !await hostScopeResolver.CanReachPropertyAsync(context.User, resource))
        {
            logger.LogDebug(
                "Host resource denied: user {UserId} does not reach the property of the row ({Permission})",
                userId, requirement.PermissionKey);
            return;
        }

        foreach (var contextKey in requirement.ContextKeys)
        {
            if (await contextAuthorizationService.HasPermissionAsync(userId, contextKey, requirement.PermissionKey))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}

/// <summary>Shorthand for the boolean outcome of a resource-based check.</summary>
public static class HostAuthorizationExtensions
{
    public static async Task<bool> IsAuthorizedAsync(
        this IAuthorizationService authorizationService,
        System.Security.Claims.ClaimsPrincipal user,
        HostResource resource,
        HostOperationRequirement operation) =>
        (await authorizationService.AuthorizeAsync(user, resource, operation)).Succeeded;
}
