using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

public sealed class OrgBillingAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Plan, entitlement, billing, branding, domain and site documents of the caller's host org: an org policy, not a
/// context one (PL-16, A1-36). Only the <b>owner</b> of the org passes, whichever rental context it works in (short-rent
/// <c>PropertyOwner</c> or long-rent <c>LongTermLandlord</c>), and the platform admin. Everyone else is refused: a
/// <c>Staff</c> collaborator, a guest, a <c>PropertyManager</c> (it runs the properties, not the plan and the invoices:
/// wave decision D12) and, with a DB membership only, any holder of a role other than the owner's (<see cref="OrgOwnerRoles"/>).
/// Like the host contexts, it waits for the host onboarding and the current consents (PL-02): refused with
/// <see cref="HostOnboarding.RequiredCode"/> until then, platform admins included, since billing an org means using it
/// as a host.
/// </summary>
/// <remarks>
/// AM-01: the policy keeps its name and evaluates <see cref="AccountContext.Permissions.BillingManage"/>
/// (<c>org.billing.manage</c>), which the account context gives to the org owner (<c>org_owner</c>) and administrator
/// (<c>org_admin</c>): a membership of the account context that holds it passes, as the roles of
/// <see cref="OrgOwnerRoles"/> do, and the token roles and the owner's rental memberships of AM-00 pass as before (every
/// existing owner keeps working). A member the org deactivated never passes.
/// </remarks>
public class OrgBillingAdminAuthorizationHandler(
    IOrgContextResolver orgContextResolver,
    IUserAuthorizationSnapshotStore snapshotStore,
    IHostOnboardingGate hostOnboardingGate) : AuthorizationHandler<OrgBillingAdminRequirement>
{
    /// <summary>
    /// JWT roles that grant billing administration: the owner of either rental context (a landlord with only long-term
    /// leases pays its plan too, PL-16) and the platform admin. Not <c>PropertyManager</c> (D12): a property manager
    /// operates the properties but does not manage plan and invoices. The DB memberships that grant it without relying
    /// on the JWT are the owner's roles of <see cref="OrgOwnerRoles"/>.
    /// </summary>
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PropertyOwner",
        "LongTermLandlord",
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

        var userId = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return;

        // Read once per request (shared with every other policy of the request): the JWT roles are not enough to pass
        // for a member the org deactivated (AM-01); the request itself is refused earlier with member_inactive, this is
        // the policy's own guard.
        var snapshot = await snapshotStore.GetAsync(userId);
        if (snapshot.IsOrgMemberDeactivated)
            return;

        if (!HasAllowedRole(context.User) && !HasAllowedMembership(snapshot))
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
    /// the Auth0 role sync was failing does not lock the host out of billing (A1-02). Only an owner's role key counts
    /// (<see cref="OrgOwnerRoles"/>): a member of the org with a membership of another role never gets through.
    /// </summary>
    private static bool HasAllowedMembership(UserAuthorizationSnapshot snapshot) =>
        snapshot is { Exists: true, IsActive: true } &&
        !snapshot.IsOrgMemberDeactivated &&
        snapshot.Role is not (UserRole.Staff or UserRole.Guest) &&
        snapshot.Memberships.Any(IsAllowedBillingMembership);

    /// <summary>
    /// The owner's (or administrator's) role key (<see cref="OrgOwnerRoles"/>), or a membership of the account context
    /// that holds <c>org.billing.manage</c>: the permission this policy evaluates (AM-01).
    /// </summary>
    private static bool IsAllowedBillingMembership(ContextAccess membership) =>
        OrgOwnerRoles.IsOwnerRole(membership.ContextKey, membership.RoleKey) ||
        (AccountContext.IsAccountContext(membership.ContextKey) &&
         membership.Permissions.Contains(AccountContext.Permissions.BillingManage, StringComparer.OrdinalIgnoreCase));

    private static bool HasAllowedRole(ClaimsPrincipal user) =>
        user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "https://casazen.app/roles") &&
            AllowedRoles.Contains(c.Value));

    private static bool HasDeniedRole(ClaimsPrincipal user) =>
        user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "https://casazen.app/roles") &&
            DeniedRoles.Contains(c.Value));
}
