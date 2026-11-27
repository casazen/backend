using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// E50/F01/US01/T01 (NF-152, AC-NF152-5, guest portal plan decision A7): who may set up the guest portal («Portale ospiti»). The permission
/// <c>guestportal.manage</c> is read from the roles the model seeds (what the migrations leave in the database) for every org role, so a grant moved
/// to the wrong role shows up here.
/// </summary>
public class GuestPortalManagePermissionTests
{
    /// <summary>
    /// Permission rule: the owner, the administrator and the property manager hold it; the collaborator and the accountant never do. The owner holds it
    /// on the token fallback too (no membership rows yet), the policy asks for exactly it in the short-rent context, and the matrix of the roles knows
    /// it (the tripwire of the capabilities).
    /// </summary>
    [Fact]
    public void GuestPortalManage_GrantedOnlyToManagingRoles_Test()
    {
        using var db = OrgTeamTestData.NewDb();
        var seeded = db.RolePermissions.Include(rp => rp.Role).AsEnumerable()
            .Where(rp => rp.Role.ContextKey == OrgRoleCatalog.ShortRent)
            .GroupBy(rp => rp.Role.RoleKey)
            .ToDictionary(g => g.Key, g => g.Select(rp => rp.PermissionKey).ToHashSet(StringComparer.Ordinal));

        bool Holds(OrgRole role) =>
            seeded.TryGetValue(OrgRoleCatalog.HostRoleKey(role, OrgRoleCatalog.ShortRent), out var permissions)
            && permissions.Contains(HostPermissions.GuestPortalManage);

        Assert.True(Holds(OrgRole.Owner));
        Assert.True(Holds(OrgRole.Admin));
        Assert.True(Holds(OrgRole.PropertyManager));
        Assert.False(Holds(OrgRole.Collaborator));
        Assert.False(Holds(OrgRole.Accountant));

        // Nowhere in the long-rent context, whatever the role.
        Assert.DoesNotContain(
            db.RolePermissions.Include(rp => rp.Role).AsEnumerable(),
            rp => rp.Role.ContextKey == OrgRoleCatalog.LongRent && rp.PermissionKey == HostPermissions.GuestPortalManage);

        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.GuestPortalManage);
        Assert.Equal(OrgRoleCatalog.ShortRent, Assert.Single(contexts));
        Assert.Equal("guestportal.manage", permission);
        Assert.Contains(CasazenPolicies.GuestPortalManage, CasazenPolicies.ContextPolicies);

        var owner = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent);
        Assert.Contains(HostPermissions.GuestPortalManage, owner.Permissions);

        Assert.Contains(new ContextPermission(OrgRoleCatalog.ShortRent, HostPermissions.GuestPortalManage), OrgCapabilityCatalog.Claims);
        Assert.DoesNotContain(HostPermissions.GuestPortalManage, HostPermissions.ShortRentFine);
    }
}
