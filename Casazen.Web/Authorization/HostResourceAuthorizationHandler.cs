using Casazen.Core.Authorization;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

/// <summary>
/// Resource-based authorization of host rows (TN-3). An operation on a <see cref="HostResource"/> succeeds only when
/// <list type="number">
/// <item>the caller's org (the same <see cref="ITenantContext"/> the EF tenant filter uses) is the row's org;</item>
/// <item>the caller holds the operation's context permission (DB membership or JWT fallback);</item>
/// <item>for rows bound to a property, the caller owns it or has an org-wide role (<see cref="HostRoles.OrgWide"/>).</item>
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

        if (resource.PropertyOwnerId is { } ownerId &&
            !string.Equals(ownerId, userId, StringComparison.Ordinal) &&
            !context.User.HasOrgWideHostAccess())
        {
            logger.LogDebug(
                "Host resource denied: user {UserId} does not own the property and has no org-wide role ({Permission})",
                userId, requirement.PermissionKey);
            return;
        }

        if (!await contextAuthorizationService.HasPermissionAsync(userId, requirement.ContextKey, requirement.PermissionKey))
            return;

        context.Succeed(requirement);
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
