using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-01: what an org role means in memberships (<see cref="OrgRoleCatalog"/>): the account row of the roles that
/// administer or read the org, the role key of each rental context, the projection of a person that works in some of them.
/// </summary>
public class OrgRoleCatalogTests
{
    private static readonly string[] BothAreas = ["short-rent", "long-rent"];

    [Theory]
    [InlineData(OrgRole.Owner, "org_owner")]
    [InlineData(OrgRole.Admin, "org_admin")]
    [InlineData(OrgRole.Accountant, "org_accountant")]
    public void AccountRoleKey_RolesThatAdministerOrReadTheOrg_HaveAnAccountRole(OrgRole role, string expected) =>
        Assert.Equal(expected, OrgRoleCatalog.AccountRoleKey(role));

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    public void AccountRoleKey_OperationalRoles_HaveNone(OrgRole role) =>
        Assert.Null(OrgRoleCatalog.AccountRoleKey(role));

    [Theory]
    [InlineData(OrgRole.Owner, "short-rent", "property_owner")]
    [InlineData(OrgRole.Owner, "long-rent", "long_term_landlord")]
    [InlineData(OrgRole.Admin, "short-rent", "property_manager")]
    [InlineData(OrgRole.Admin, "long-rent", "property_manager")]
    [InlineData(OrgRole.PropertyManager, "short-rent", "property_manager")]
    [InlineData(OrgRole.PropertyManager, "long-rent", "property_manager")]
    [InlineData(OrgRole.Collaborator, "short-rent", "staff")]
    [InlineData(OrgRole.Collaborator, "long-rent", "staff")]
    [InlineData(OrgRole.Accountant, "short-rent", "accountant")]
    [InlineData(OrgRole.Accountant, "long-rent", "accountant")]
    [InlineData(OrgRole.Collaborator, "LONG-RENT", "staff")]
    public void HostRoleKey_RoleInARentalContext_IsTheRoleKeyOfTheCatalog(OrgRole role, string context, string expected) =>
        Assert.Equal(expected, OrgRoleCatalog.HostRoleKey(role, context));

    [Theory]
    [InlineData("account")]
    [InlineData("admin")]
    [InlineData("supplier")]
    [InlineData("")]
    public void HostRoleKey_ContextThatIsNotRental_Throws(string context) =>
        Assert.Throws<ArgumentException>(() => OrgRoleCatalog.HostRoleKey(OrgRole.Admin, context));

    [Fact]
    public void ProjectionOf_Admin_IsTheAccountRowAndAPropertyManagerRowPerArea()
    {
        var projection = OrgRoleCatalog.ProjectionOf(OrgRole.Admin, BothAreas);

        Assert.Equal(
            [
                new ProjectedRole("account", "org_admin"),
                new ProjectedRole("short-rent", "property_manager"),
                new ProjectedRole("long-rent", "property_manager"),
            ],
            projection);
    }

    [Fact]
    public void ProjectionOf_CollaboratorInOneArea_IsOnlyThatStaffRow() =>
        Assert.Equal([new ProjectedRole("long-rent", "staff")], OrgRoleCatalog.ProjectionOf(OrgRole.Collaborator, ["long-rent"]));

    [Fact]
    public void ProjectionOf_Accountant_AddsTheBillingReadAccountRow() =>
        Assert.Equal(
            [new ProjectedRole("account", "org_accountant"), new ProjectedRole("short-rent", "accountant")],
            OrgRoleCatalog.ProjectionOf(OrgRole.Accountant, ["short-rent"]));

    [Fact]
    public void ProjectionOf_OwnerWithoutAreas_IsOnlyTheAccountRow() =>
        Assert.Equal([new ProjectedRole("account", "org_owner")], OrgRoleCatalog.ProjectionOf(OrgRole.Owner, []));

    [Fact]
    public void ProjectionOf_AreasThatAreNotRentalContextsOrRepeated_AreIgnored()
    {
        var projection = OrgRoleCatalog.ProjectionOf(OrgRole.PropertyManager, ["admin", "account", "SHORT-RENT", "short-rent", "supplier"]);

        Assert.Equal([new ProjectedRole("short-rent", "property_manager")], projection);
    }

    [Fact]
    public void SeededRoles_HaveContiguousIdsFromFour_AndOneRowPerContextAndKey()
    {
        var roles = OrgRoleCatalog.SeededRoles;

        Assert.Equal(Enumerable.Range(4, roles.Count), roles.Select(r => r.Id));
        Assert.Equal(roles.Count, roles.Select(r => (r.ContextKey, r.RoleKey)).Distinct().Count());
        Assert.All(roles, role => Assert.Contains(role.ContextKey, new[] { "account", "short-rent", "long-rent" }));
        Assert.All(roles, role => Assert.NotEmpty(role.Permissions));
        Assert.All(roles, role => Assert.Equal(role.Permissions.Count, role.Permissions.Distinct().Count()));
    }

    [Fact]
    public void SeededRoles_CoverEveryRoleOfEveryProjection()
    {
        // Whatever role and areas a person has, every role row the projection points to exists: the service never has
        // to fail for a role that was not seeded. The owner's rental keys (ids 1 and 2) are the seed of the context
        // authorization.
        var seeded = OrgRoleCatalog.SeededRoles.Select(r => (r.ContextKey, r.RoleKey))
            .Concat([("short-rent", "property_owner"), ("long-rent", "long_term_landlord")])
            .ToHashSet();

        foreach (var role in Enum.GetValues<OrgRole>())
        {
            foreach (var projected in OrgRoleCatalog.ProjectionOf(role, BothAreas))
                Assert.Contains((projected.ContextKey, projected.RoleKey), seeded);
        }
    }

    [Fact]
    public void SeededRoles_OnlyTheOwnerAndTheAdminAdministerTheOrg()
    {
        var billingManage = AccountContext.Permissions.BillingManage;
        var membersManage = AccountContext.Permissions.MembersManage;

        var administrators = OrgRoleCatalog.SeededRoles
            .Where(r => r.Permissions.Contains(billingManage) || r.Permissions.Contains(membersManage))
            .Select(r => (r.ContextKey, r.RoleKey))
            .ToList();

        Assert.Equal([("account", "org_owner"), ("account", "org_admin")], administrators);
    }

    [Fact]
    public void SeededRoles_PropertyManagerHasSuppliersButNoBillingNorTeam()
    {
        foreach (var role in OrgRoleCatalog.SeededRoles.Where(r => r.RoleKey == "property_manager"))
        {
            Assert.Contains(AccountContext.Permissions.SuppliersManage, role.Permissions);
            Assert.DoesNotContain(role.Permissions, p => p.StartsWith("org.billing", StringComparison.Ordinal));
            Assert.DoesNotContain(role.Permissions, p => p.StartsWith("org.members", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SeededRoles_CollaboratorAndAccountantNeverWritePricesBookingsOrPayments()
    {
        var forbidden = new[] { "property.write", "booking.write", "payment.write", "payment.read" };

        foreach (var role in OrgRoleCatalog.SeededRoles.Where(r => r.RoleKey == "staff"))
            Assert.Empty(role.Permissions.Intersect(forbidden));

        foreach (var role in OrgRoleCatalog.SeededRoles.Where(r => r.RoleKey == "accountant"))
        {
            Assert.DoesNotContain(role.Permissions, p => p.EndsWith(".write", StringComparison.Ordinal));
            Assert.DoesNotContain(role.Permissions, p => p.EndsWith(".create", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SeededRoles_LongRentCollaboratorOnlyReadsProperties() =>
        Assert.Equal(["property.read"], OrgRoleCatalog.SeededRoles.Single(r => r is { ContextKey: "long-rent", RoleKey: "staff" }).Permissions);

    private static IReadOnlyList<string> PermissionsOf(string context, string roleKey) =>
        OrgRoleCatalog.SeededRoles.Single(r => r.ContextKey == context && r.RoleKey == roleKey).Permissions;

    [Theory]
    [InlineData("servicerequest.write")]
    [InlineData("guest.manage")]
    [InlineData("alloggiati.submit")]
    public void SeededRoles_FinerPermissionsOfAm03_ThePropertyManagerKeepsEverythingItDid(string permission) =>
        Assert.Contains(permission, PermissionsOf("short-rent", "property_manager"));

    [Fact]
    public void SeededRoles_Collaborator_CreatesInterventionsButHasNoPricesCinBookingsPaymentsOrErasure()
    {
        var collaborator = PermissionsOf("short-rent", "staff");

        Assert.Equal(
            ["booking.read", "guest.read", "guest.write", "property.read", "servicerequest.write"],
            collaborator.Order(StringComparer.Ordinal).ToArray());
        // The carve-outs are exactly what keeps the collaborator out of the broad permissions.
        foreach (var broad in new[] { "property.write", "booking.write", "payment.read", "payment.write", "guest.manage", "alloggiati.submit", "ota.write" })
            Assert.DoesNotContain(broad, collaborator);
    }

    [Theory]
    [InlineData("short-rent", "accountant")]
    [InlineData("long-rent", "accountant")]
    [InlineData("long-rent", "staff")]
    [InlineData("long-rent", "property_manager")]
    public void SeededRoles_FinerPermissionsOfAm03_AreOnlyInTheShortRentRolesThatAct(string context, string roleKey)
    {
        var permissions = PermissionsOf(context, roleKey);

        Assert.DoesNotContain("servicerequest.write", permissions);
        Assert.DoesNotContain("guest.manage", permissions);
        Assert.DoesNotContain("alloggiati.submit", permissions);
    }

    [Fact]
    public void FinerPermissions_TheOwnerKeepsThemOnTheTokenFallbackToo()
    {
        var owner = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]).Single(c => c.ContextKey == "short-rent");

        foreach (var permission in HostPermissions.ShortRentFine)
            Assert.Contains(permission, owner.Permissions);
    }

    [Fact]
    public void AccountContext_IsAHostContext_SoItWaitsForTheOnboarding()
    {
        Assert.True(HostOnboarding.IsHostContext("account"));
        Assert.True(HostOnboarding.IsHostContext("ACCOUNT"));
        // The staff console is not an org matter.
        Assert.False(HostOnboarding.IsHostContext("admin"));
    }
}
