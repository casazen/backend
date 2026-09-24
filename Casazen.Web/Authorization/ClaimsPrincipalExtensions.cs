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

    /// <summary>True when the caller sees every property of the org (<see cref="HostRoles.OrgWide"/>).</summary>
    public static bool HasOrgWideHostAccess(this ClaimsPrincipal user) =>
        user.GetRoles().Overlaps(HostRoles.OrgWide);

    /// <summary>
    /// The caller's scope on the host data of <paramref name="orgId"/> for list queries: the whole org for org-wide
    /// roles, otherwise only the properties the caller owns. <c>null</c> when the caller has no user id (never
    /// widened to the whole org).
    /// </summary>
    public static HostScope? GetHostScope(this ClaimsPrincipal user, Guid orgId)
    {
        if (user.HasOrgWideHostAccess())
            return new HostScope(orgId, null);

        var userId = user.GetUserId();
        return string.IsNullOrWhiteSpace(userId) ? null : new HostScope(orgId, userId);
    }
}
