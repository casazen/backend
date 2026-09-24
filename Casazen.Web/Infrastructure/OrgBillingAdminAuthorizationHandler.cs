using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

public sealed class OrgBillingAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Plan, billing and domain of the caller's host org. Like the host contexts, it waits for the host onboarding and the
/// current consents (PL-02): refused with <see cref="HostOnboarding.RequiredCode"/> until then, platform admins included,
/// since billing an org means using it as a host.
/// </summary>
public class OrgBillingAdminAuthorizationHandler(
    IOrgContextResolver orgContextResolver,
    IUserAuthorizationSnapshotStore snapshotStore,
    IHostOnboardingGate hostOnboardingGate) : AuthorizationHandler<OrgBillingAdminRequirement>
{
    /// <summary>DB context memberships that grant billing administration without relying on JWT roles.</summary>
    private static readonly HashSet<string> AllowedMembershipContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "short-rent",
        "admin",
    };

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

        if (!HasAllowedRole(context.User) && !await HasAllowedMembershipAsync(context.User))
            return;

        var userId = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return;

        if (!(await hostOnboardingGate.GetStatusAsync(userId)).IsComplete)
        {
            context.Fail(new AuthorizationFailureReason(this, HostOnboarding.RequiredCode));
            return;
        }

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync();
        if (orgId is null)
            return;

        context.Succeed(requirement);
    }

    /// <summary>
    /// Falls back to the DB memberships written at onboarding / role change, so a JWT issued while
    /// the Auth0 role sync was failing does not lock the host out of billing (A1-02).
    /// </summary>
    private async Task<bool> HasAllowedMembershipAsync(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var snapshot = await snapshotStore.GetAsync(userId);
        return snapshot is { Exists: true, IsActive: true } &&
               snapshot.Memberships.Any(m => AllowedMembershipContexts.Contains(m.ContextKey));
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
