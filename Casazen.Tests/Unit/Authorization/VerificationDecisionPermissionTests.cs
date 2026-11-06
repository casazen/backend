using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// E03/F04/US02/T01 (AC-NF24-3): who may decide the identity verification of a guest. The permission <c>verification.decision</c> is read from
/// the roles the model seeds (what the migrations leave in the database) for every org role, so a grant moved to the wrong role shows up here.
/// </summary>
public class VerificationDecisionPermissionTests
{
    /// <summary>Permission rule: the owner, the administrator and the property manager decide; the collaborator and the accountant never do.</summary>
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

        // The policy of the operator queue asks for exactly this permission, in the short-rent context.
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.VerificationDecision);
        Assert.Equal(OrgRoleCatalog.ShortRent, Assert.Single(contexts));
        Assert.Equal("verification.decision", permission);
        Assert.Equal(HostPermissions.VerificationDecision, permission);

        // An owner on the token fallback (no membership rows yet) decides too, like the seeded role.
        var owner = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == OrgRoleCatalog.ShortRent);
        Assert.Contains(HostPermissions.VerificationDecision, owner.Permissions);
    }
}
