using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Web.Authorization;

namespace Casazen.Web.Infrastructure;

internal sealed record HostCaller(string UserId, Guid OrgId, HostScope Scope);

internal static class HostCallerResolver
{
    public static async Task<HostCaller?> CallerAsync(
        ClaimsPrincipal user,
        IOrgContextResolver orgContextResolver,
        IHostScopeResolver hostScopeResolver,
        CancellationToken cancellationToken)
    {
        if (user.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(user, orgId, cancellationToken) is not { } scope)
        {
            return null;
        }

        return new HostCaller(userId, orgId, scope);
    }

    public static async Task<Guid?> OrgWideAsync(
        ClaimsPrincipal user,
        IOrgContextResolver orgContextResolver,
        IHostScopeResolver hostScopeResolver,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(user, orgId.Value, cancellationToken);
        return scope is { IsOrgWide: true } ? orgId : null;
    }
}
