using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

public class ApprovalDecidePermissionTests
{
    [Fact]
    public void ApprovalDecide_OwnerAndManagerHaveIt_CollaboratorDoesNot_Test()
    {
        using var db = OrgTeamTestData.NewDb();
        var seeded = db.RolePermissions.Include(rp => rp.Role).AsEnumerable()
            .Where(rp => rp.Role.ContextKey == OrgRoleCatalog.ShortRent)
            .GroupBy(rp => rp.Role.RoleKey)
            .ToDictionary(g => g.Key, g => g.Select(rp => rp.PermissionKey).ToHashSet(StringComparer.Ordinal));

        bool Holds(OrgRole role) =>
            seeded.TryGetValue(OrgRoleCatalog.HostRoleKey(role, OrgRoleCatalog.ShortRent), out var permissions)
            && permissions.Contains(HostPermissions.ApprovalDecide);

        Assert.True(Holds(OrgRole.Owner));
        Assert.True(Holds(OrgRole.Admin));
        Assert.True(Holds(OrgRole.PropertyManager));
        Assert.False(Holds(OrgRole.Collaborator));
        Assert.False(Holds(OrgRole.Accountant));

        Assert.DoesNotContain(
            db.RolePermissions.Include(rp => rp.Role).AsEnumerable().Where(rp => rp.Role.ContextKey == OrgRoleCatalog.LongRent),
            rp => rp.PermissionKey == HostPermissions.ApprovalDecide);

        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.ApprovalDecide);
        Assert.Equal(OrgRoleCatalog.ShortRent, Assert.Single(contexts));
        Assert.Equal("approval.decide", permission);
        Assert.Equal(HostPermissions.ApprovalDecide, permission);
        Assert.Contains(CasazenPolicies.ApprovalDecide, CasazenPolicies.ContextPolicies);

        var owner = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent);
        Assert.Contains(HostPermissions.ApprovalDecide, owner.Permissions);

        Assert.Contains(new ContextPermission(OrgRoleCatalog.ShortRent, HostPermissions.ApprovalDecide), OrgCapabilityCatalog.Claims);
    }
}
