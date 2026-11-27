using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Web.Authorization;

namespace Casazen.Web.Infrastructure;

/// <summary>The caller of a host endpoint: who it is, the org it acts in and what it reaches inside that org.</summary>
/// <param name="UserId">The id of the caller's user.</param>
/// <param name="OrgId">The org of the caller.</param>
/// <param name="Scope">What the caller reaches inside the org (<see cref="IHostScopeResolver"/>).</param>
internal sealed record HostCaller(string UserId, Guid OrgId, HostScope Scope);

/// <summary>
/// What the host controllers read of the caller in the same way: the org and the scope come from the caller's membership
/// (<see cref="IOrgContextResolver"/>, <see cref="IHostScopeResolver"/>), never from the request.
/// </summary>
internal static class HostCallerResolver
{
    /// <summary>The caller's id, org and scope; <c>null</c> when it has no org or reaches nothing in it.</summary>
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

    /// <summary>The caller's org, only when its reach is the whole org; <c>null</c> when it has no org or reaches only some properties of it.</summary>
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
