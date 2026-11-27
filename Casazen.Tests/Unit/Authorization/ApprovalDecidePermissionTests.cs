using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// E05/F04/US01/T01 (AC-NF68-5): who may decide the items of the approval queue. The permission <c>approval.decide</c> is read from the roles the
/// model seeds (what the migrations leave in the database) for every org role, so a grant moved to the wrong role shows up here.
/// </summary>
public class ApprovalDecidePermissionTests
{
    /// <summary>
    /// Permission rule: the owner, the administrator and the property manager decide the items of the queue (a message goes out under the host's name,
    /// a campaign is approved); the collaborator and the accountant never do.
    /// </summary>
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

        // The long-term context has no queue: nobody holds the permission there.
        Assert.DoesNotContain(
            db.RolePermissions.Include(rp => rp.Role).AsEnumerable().Where(rp => rp.Role.ContextKey == OrgRoleCatalog.LongRent),
            rp => rp.PermissionKey == HostPermissions.ApprovalDecide);

        // The policy of the queue asks for exactly this permission, in the short-rent context, and is registered at startup.
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.ApprovalDecide);
        Assert.Equal(OrgRoleCatalog.ShortRent, Assert.Single(contexts));
        Assert.Equal("approval.decide", permission);
        Assert.Equal(HostPermissions.ApprovalDecide, permission);
        Assert.Contains(CasazenPolicies.ApprovalDecide, CasazenPolicies.ContextPolicies);

        // An owner on the token fallback (no membership rows yet) decides too, like the seeded role.
        var owner = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent);
        Assert.Contains(HostPermissions.ApprovalDecide, owner.Permissions);

        // The matrix of the roles knows it (the tripwire of the capabilities fails for a permission a seeded role holds and the matrix does not).
        Assert.Contains(new ContextPermission(OrgRoleCatalog.ShortRent, HostPermissions.ApprovalDecide), OrgCapabilityCatalog.Claims);
    }
}
