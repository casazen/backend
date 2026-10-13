using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Authorization;

/// <summary>
/// Caller identity helpers shared by controllers and handlers (TN-3), in place of the <c>GetUserId</c> /
/// <c>GetUserRoles</c> copies that each controller used to carry.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>Custom Auth0 claim with the roles (JSON array or plain value).</summary>
    public const string Auth0RolesClaim = "https://casazen.app/roles";

    /// <summary>The Auth0 subject (<c>sub</c>, or the mapped name identifier); <c>null</c> when anonymous.</summary>
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>JWT roles: the standard role claims plus the parsed Auth0 roles claim.</summary>
    public static IReadOnlySet<string> GetRoles(this ClaimsPrincipal user)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        roles.UnionWith(user.FindAll(ClaimTypes.Role).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)));
        roles.UnionWith(Auth0RolesClaimParser.Parse(user.FindAll(Auth0RolesClaim).Select(c => c.Value)));
        return roles;
    }

    /// <summary>
    /// The caller's scope on the host data of <paramref name="orgId"/> for list queries (AM-03): what its org membership
    /// says (every property, or only the ones it was given), read from the database through the authorization snapshot;
    /// the token decides only for an account that is in no org team (<see cref="IHostScopeResolver"/>). <c>null</c> when the
    /// caller has no user id, is deactivated or belongs to another org: never widened to the whole org.
    /// </summary>
    public static Task<HostScope?> ResolveHostScopeAsync(
        this IHostScopeResolver resolver,
        ClaimsPrincipal user,
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(user);

        var userId = user.GetUserId();
        return string.IsNullOrWhiteSpace(userId)
            ? Task.FromResult<HostScope?>(null)
            : resolver.ResolveAsync(userId, user.GetRoles(), orgId, cancellationToken);
    }

    /// <summary>
    /// True when the caller reaches the property of a row (<see cref="IHostScopeResolver.CanReachPropertyAsync"/>); the
    /// check behind <see cref="HostResourceAuthorizationHandler"/>.
    /// </summary>
    public static Task<bool> CanReachPropertyAsync(
        this IHostScopeResolver resolver,
        ClaimsPrincipal user,
        HostResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(resource);

        var userId = user.GetUserId();
        return string.IsNullOrWhiteSpace(userId)
            ? Task.FromResult(false)
            : resolver.CanReachPropertyAsync(
                userId, user.GetRoles(), resource.OrgId, resource.PropertyId, resource.PropertyOwnerId, cancellationToken);
    }
}
