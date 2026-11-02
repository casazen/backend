using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

/// <summary>The agency permission (<see cref="AgencyPermissions"/>) an <c>agency.*</c> policy requires on top of the agency tier (DEB-01).</summary>
public sealed record AgencyPermissionRequirement(string PermissionKey) : IAuthorizationRequirement;

/// <summary>
/// The role half of the <c>agency.*</c> policies (DEB-01, 2026-10-31): the caller's org role must hold the permission of the policy
/// (<see cref="OrgRoleCatalog.AgencyPermissionsOf"/>). <see cref="AgencyTierAuthorizationHandler"/> checks the plan and the downgrade on
/// the same policies; both must pass. Before this, the plan was the whole check, so any member of an agency org (a collaborator, an
/// accountant) read the full IBAN of the owners (<c>GET api/owners/{id}/iban</c>) and created wire batches.
/// </summary>
/// <remarks>
/// <para>The org role is the one of the caller's <see cref="Casazen.Core.Entities.OrgMember"/> row (AM-01, the source of truth, as for
/// <c>OrgBillingAdmin</c>): an active member of the org of the request, whatever the token says. A member of another org, a deactivated
/// member, an inactive account and the <c>Staff</c> and <c>Guest</c> roles hold nothing.</para>
/// <para>A user in no org team (an owner from before the backfill of the members) is the owner of its own org when its token or its
/// rental membership says so (<c>PropertyOwner</c>, <c>LongTermLandlord</c>, <see cref="OrgOwnerRoles"/>), so the owner of an existing
/// org is not locked out; nobody else is. Like the host contexts, it waits for the host onboarding and the current consents (PL-02): refused
/// with <see cref="HostOnboarding.RequiredCode"/> until then.</para>
/// </remarks>
public class AgencyPermissionAuthorizationHandler(
    IUserAuthorizationSnapshotStore snapshotStore,
    IOrgContextResolver orgContextResolver,
    IHostOnboardingGate hostOnboardingGate,
    ILogger<AgencyPermissionAuthorizationHandler> logger) : AuthorizationHandler<AgencyPermissionRequirement>
{
    private static readonly HashSet<string> OwnerTokenRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PropertyOwner",
        "LongTermLandlord",
    };

    private static readonly HashSet<string> DeniedTokenRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Staff",
        "Guest",
    };

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AgencyPermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var userId = context.User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync();
        if (orgId is null)
            return;

        var snapshot = await snapshotStore.GetAsync(userId);
        var role = RoleOf(context.User, snapshot, orgId.Value);
        if (role is null || !OrgRoleCatalog.AgencyPermissionsOf(role.Value).Contains(requirement.PermissionKey))
        {
            logger.LogDebug(
                "Agency permission {PermissionKey} denied to user {UserId} (org role {OrgRole})",
                requirement.PermissionKey, userId, role?.ToString() ?? "none");
            return;
        }

        if (!(await hostOnboardingGate.GetStatusAsync(userId)).IsComplete)
        {
            context.Fail(new AuthorizationFailureReason(this, HostOnboarding.RequiredCode));
            return;
        }

        context.Succeed(requirement);
    }

    /// <summary>The org role the caller acts with in <paramref name="orgId"/>; <c>null</c> when it holds none there (see remarks).</summary>
    private static OrgRole? RoleOf(ClaimsPrincipal user, UserAuthorizationSnapshot snapshot, Guid orgId)
    {
        if (snapshot is { Exists: true, IsActive: false } || snapshot.Role is UserRole.Staff or UserRole.Guest)
            return null;

        var tokenRoles = user.GetRoles();
        if (tokenRoles.Any(DeniedTokenRoles.Contains))
            return null;

        if (snapshot.OrgMember is { } member)
            return member.OrgId == orgId && member.Status == OrgMemberStatus.Active ? member.Role : null;

        var isOwner = tokenRoles.Any(OwnerTokenRoles.Contains) ||
                      snapshot.Memberships.Any(m =>
                          OrgRoleCatalog.IsRentalContext(m.ContextKey) && OrgOwnerRoles.IsOwnerRole(m.ContextKey, m.RoleKey));
        return isOwner ? OrgRole.Owner : null;
    }
}
