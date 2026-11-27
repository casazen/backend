using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

public class VerificationDecisionPermissionTests
{
    [Fact]
    public void VerificationDecision_GrantedOnlyToDecidingRoles_Test()
    {
        using var db = OrgTeamTestData.NewDb();
        var seeded = db.RolePermissions.Include(rp => rp.Role).AsEnumerable()
            .Where(rp => rp.Role.ContextKey == OrgRoleCatalog.ShortRent)
            .GroupBy(rp => rp.Role.RoleKey)
            .ToDictionary(g => g.Key, g => g.Select(rp => rp.PermissionKey).ToHashSet(StringComparer.Ordinal));

        bool Holds(OrgRole role) =>
            seeded.TryGetValue(OrgRoleCatalog.HostRoleKey(role, OrgRoleCatalog.ShortRent), out var permissions)
            && permissions.Contains(HostPermissions.VerificationDecision);

        Assert.True(Holds(OrgRole.Owner));
        Assert.True(Holds(OrgRole.Admin));
        Assert.True(Holds(OrgRole.PropertyManager));
        Assert.False(Holds(OrgRole.Collaborator));
        Assert.False(Holds(OrgRole.Accountant));

        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.VerificationDecision);
        Assert.Equal(OrgRoleCatalog.ShortRent, Assert.Single(contexts));
        Assert.Equal("verification.decision", permission);
        Assert.Equal(HostPermissions.VerificationDecision, permission);

        var owner = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent);
        Assert.Contains(HostPermissions.VerificationDecision, owner.Permissions);
    }
}
