using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-00 (S1, D12): who passes the org policy <c>OrgBillingAdmin</c> (plan, billing, Connect, branding, domain, site
/// documents), for every combination of JWT roles, DB role and DB memberships. Only the owner of the org and the
/// platform admin pass; a member of the org (any other role key) never does, with or without a <c>Staff</c> claim, and a
/// <c>PropertyManager</c> neither.
/// </summary>
public class OrgBillingAdminAuthorizationHandlerTests
{
    private const string Sub = "auth0|billing-matrix";

    /// <summary>
    /// <c>name, JWT roles, DB role of the user, DB memberships as "context/roleKey", passes the policy</c>. Written out
    /// by hand, not derived from the handler's own rules.
    /// </summary>
    public static IEnumerable<object[]> Scenarios()
    {
        // ── JWT roles only (no DB membership) ───────────────────────────────────────────────
        yield return Row("jwt: no role", [], UserRole.None, [], false);
        yield return Row("jwt: PropertyOwner", ["PropertyOwner"], UserRole.PropertyOwner, [], true);
        yield return Row("jwt: LongTermLandlord", ["LongTermLandlord"], UserRole.LongTermLandlord, [], true);
        yield return Row("jwt: Admin", ["Admin"], UserRole.Admin, [], true);
        yield return Row("jwt: PropertyOwner + PropertyManager", ["PropertyOwner", "PropertyManager"], UserRole.PropertyOwner, [], true);
        yield return Row("jwt: LongTermLandlord + PropertyManager", ["LongTermLandlord", "PropertyManager"], UserRole.LongTermLandlord, [], true);
        // D12: a property manager runs the properties, not the plan and the invoices.
        yield return Row("jwt: PropertyManager only (D12)", ["PropertyManager"], UserRole.PropertyManager, [], false);
        yield return Row("jwt: PropertyManager only, DB role None", ["PropertyManager"], UserRole.None, [], false);
        yield return Row("jwt: PropertyManager + Staff", ["PropertyManager", "Staff"], UserRole.PropertyManager, [], false);
        yield return Row("jwt: Staff", ["Staff"], UserRole.Staff, [], false);
        yield return Row("jwt: Guest", ["Guest"], UserRole.Guest, [], false);
        yield return Row("jwt: Supplier", ["Supplier"], UserRole.Supplier, [], false);
        yield return Row("jwt: PropertyOwner + Staff (a denied role wins)", ["PropertyOwner", "Staff"], UserRole.PropertyOwner, [], false);
        yield return Row("jwt: LongTermLandlord + Guest (a denied role wins)", ["LongTermLandlord", "Guest"], UserRole.LongTermLandlord, [], false);

        // ── DB memberships only (the JWT carries no role) ───────────────────────────────────
        yield return Row("db: short-rent/property_owner", [], UserRole.PropertyOwner, ["short-rent/property_owner"], true);
        yield return Row("db: long-rent/long_term_landlord", [], UserRole.LongTermLandlord, ["long-rent/long_term_landlord"], true);
        yield return Row("db: admin/platform_admin", [], UserRole.Admin, ["admin/platform_admin"], true);
        yield return Row("db: both owner memberships", [], UserRole.PropertyOwner, ["short-rent/property_owner", "long-rent/long_term_landlord"], true);
        yield return Row("db: owner membership, DB role None", [], UserRole.None, ["short-rent/property_owner"], true);

        // A member of the org: any other role key of a host context, whatever the DB role of the user.
        yield return Row("db: short-rent/property_manager", [], UserRole.None, ["short-rent/property_manager"], false);
        yield return Row("db: short-rent/staff", [], UserRole.None, ["short-rent/staff"], false);
        yield return Row("db: short-rent/accountant", [], UserRole.None, ["short-rent/accountant"], false);
        yield return Row("db: short-rent/bk09_collaborator", [], UserRole.None, ["short-rent/bk09_collaborator"], false);
        yield return Row("db: long-rent/staff", [], UserRole.None, ["long-rent/staff"], false);
        yield return Row("db: long-rent/accountant", [], UserRole.None, ["long-rent/accountant"], false);
        yield return Row("db: long-rent/property_manager", [], UserRole.None, ["long-rent/property_manager"], false);
        yield return Row("db: staff in both rental contexts", [], UserRole.None, ["short-rent/staff", "long-rent/staff"], false);
        yield return Row("db: member, DB role PropertyManager", [], UserRole.PropertyManager, ["short-rent/property_manager"], false);
        yield return Row("db: member, DB role Staff", [], UserRole.Staff, ["short-rent/staff"], false);
        yield return Row("db: member, DB role PropertyOwner", [], UserRole.PropertyOwner, ["short-rent/staff"], false);

        // A role key is the owner's only in its own context; other contexts never give billing rights.
        yield return Row("db: owner key of the other context (short-rent/long_term_landlord)", [], UserRole.None, ["short-rent/long_term_landlord"], false);
        yield return Row("db: owner key of the other context (long-rent/property_owner)", [], UserRole.None, ["long-rent/property_owner"], false);
        yield return Row("db: platform_admin key in a rental context", [], UserRole.None, ["short-rent/platform_admin"], false);
        yield return Row("db: admin context, role other than platform_admin", [], UserRole.None, ["admin/support"], false);
        yield return Row("db: supplier/supplier", [], UserRole.Supplier, ["supplier/supplier"], false);

        // The DB role of the user still vetoes an owner membership (Staff and Guest are never billing administrators).
        yield return Row("db: owner membership but DB role Staff", [], UserRole.Staff, ["short-rent/property_owner"], false);
        yield return Row("db: owner membership but DB role Guest", [], UserRole.Guest, ["short-rent/property_owner"], false);

        // ── JWT and DB together ─────────────────────────────────────────────────────────────
        yield return Row("both: PropertyManager jwt + property_manager membership", ["PropertyManager"], UserRole.PropertyManager, ["short-rent/property_manager"], false);
        yield return Row("both: PropertyManager jwt + staff membership", ["PropertyManager"], UserRole.None, ["short-rent/staff"], false);
        yield return Row("both: PropertyManager jwt + owner membership", ["PropertyManager"], UserRole.PropertyOwner, ["short-rent/property_owner"], true);
        yield return Row("both: Staff jwt + owner membership", ["Staff"], UserRole.PropertyOwner, ["short-rent/property_owner"], false);
        yield return Row("both: Guest jwt + owner membership", ["Guest"], UserRole.PropertyOwner, ["short-rent/property_owner"], false);
        yield return Row("both: PropertyOwner jwt + owner membership", ["PropertyOwner"], UserRole.PropertyOwner, ["short-rent/property_owner"], true);
        yield return Row("both: Supplier jwt + staff membership", ["Supplier"], UserRole.None, ["short-rent/staff"], false);

        // ── The account context (AM-01): the owner and the administrator hold org.billing.manage ───────────
        yield return Row("db: account/org_owner", [], UserRole.None, ["account/org_owner"], true);
        yield return Row("db: account/org_admin", [], UserRole.None, ["account/org_admin"], true);
        yield return Row("db: account/org_admin + short-rent/property_manager (the administrator)", [], UserRole.None, ["account/org_admin", "short-rent/property_manager"], true);
        // The accountant reads the invoices and manages nothing; the other roles have no account membership at all.
        yield return Row("db: account/org_accountant", [], UserRole.None, ["account/org_accountant"], false);
        yield return Row("db: account/org_accountant + short-rent/accountant (the accountant)", [], UserRole.None, ["account/org_accountant", "short-rent/accountant"], false);
        yield return Row("db: an owner key in the account context (account/property_owner)", [], UserRole.None, ["account/property_owner"], false);
        yield return Row("db: account/org_owner but DB role Staff", [], UserRole.Staff, ["account/org_owner"], false);
        yield return Row("both: Staff jwt + account/org_admin (a denied role wins)", ["Staff"], UserRole.None, ["account/org_admin"], false);
        yield return Row("both: PropertyManager jwt + account/org_admin", ["PropertyManager"], UserRole.None, ["account/org_admin"], true);
        yield return Row("both: PropertyManager jwt + account/org_accountant", ["PropertyManager"], UserRole.None, ["account/org_accountant"], false);
    }

    [Theory]
    [InlineData("org.billing.manage", true)]
    [InlineData("org.billing.read", false)]
    [InlineData("org.members.manage", false)]
    public async Task HandleAsync_AccountMembershipOfAnyRole_PassesOnlyWithBillingManage(string permission, bool allowed)
    {
        // The policy evaluates org.billing.manage: a role of the account context that holds it passes without being one
        // of the known role keys (a role added later cannot be forgotten by the policy), one that does not never does.
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.None,
            SupplierOrgId: null,
            Memberships: [new ContextAccess("account", "Amministrazione", "org_billing_clerk", [permission], "/app/account")]);
        var handler = CreateHandler(snapshot);
        var context = Context([]);

        await handler.HandleAsync(context);

        Assert.Equal(allowed, context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_BillingManageOutsideTheAccountContext_DoesNotPass()
    {
        // org.billing.manage counts in the account context only, like every permission in its own context.
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.None,
            SupplierOrgId: null,
            Memberships: [new ContextAccess("short-rent", "Affitti brevi", "bk09_collaborator", ["org.billing.manage"], "/app/short-rent")]);
        var handler = CreateHandler(snapshot);
        var context = Context([]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData("account", "org_owner")]
    [InlineData("account", "org_admin")]
    [InlineData("short-rent", "property_owner")]
    public async Task HandleAsync_DeactivatedMember_NeverPasses(string contextKey, string roleKey)
    {
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.None,
            SupplierOrgId: null,
            Memberships: [Access(contextKey, roleKey)],
            OrgMember: new OrgMemberSnapshot(Guid.NewGuid(), OrgRole.Admin, OrgMemberStatus.Deactivated));
        var handler = CreateHandler(snapshot);
        var context = Context([new Claim(ClaimTypes.Role, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(OrgRole.Collaborator, "short-rent", "staff")]
    [InlineData(OrgRole.PropertyManager, "short-rent", "property_manager")]
    [InlineData(OrgRole.Accountant, "account", "org_accountant")]
    public async Task HandleAsync_OrgMemberWithLeftoverOwnerClaim_IsRefused(OrgRole role, string contextKey, string roleKey)
    {
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.PropertyOwner,
            SupplierOrgId: null,
            Memberships: [Access(contextKey, roleKey)],
            OrgMember: new OrgMemberSnapshot(Guid.NewGuid(), role, OrgMemberStatus.Active));
        var handler = CreateHandler(snapshot);
        // The platform admin role is not an org role and always counts (AM-02, tested below): it is left out here.
        var context = Context(
        [
            new Claim(ClaimTypes.Role, "PropertyOwner"),
            new Claim(ClaimTypes.Role, "LongTermLandlord"),
            new Claim(ClaimTypes.Role, "PropertyManager"),
        ]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_OrgOwner_PassesFromTheOrgRoleWithoutAToken()
    {
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.PropertyOwner,
            SupplierOrgId: null,
            Memberships: [],
            OrgMember: new OrgMemberSnapshot(Guid.NewGuid(), OrgRole.Owner, OrgMemberStatus.Active));
        var handler = CreateHandler(snapshot);
        var context = Context([]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_ActiveMemberOfTheAccount_PassesAsBefore()
    {
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.None,
            SupplierOrgId: null,
            Memberships: [Access("account", "org_admin")],
            OrgMember: new OrgMemberSnapshot(Guid.NewGuid(), OrgRole.Admin, OrgMemberStatus.Active));
        var handler = CreateHandler(snapshot);
        var context = Context([]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    // ─── AM-02: a token role left over by the move to another org ──────────────────────────────────────

    private static UserAuthorizationSnapshot MemberSnapshot(OrgRole role, params string[] memberships) => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: memberships.Select(m => m.Split('/')).Select(parts => Access(parts[0], parts[1])).ToList(),
        OrgMember: new OrgMemberSnapshot(Guid.NewGuid(), role, OrgMemberStatus.Active));

    /// <summary>
    /// A person who left an empty org of its own for another keeps the Auth0 roles of the onboarding until they are removed
    /// (they are, right after the acceptance, and by an operator if Auth0 failed): a role left in the token must never make
    /// a collaborator the billing administrator of the org it joined.
    /// </summary>
    [Theory]
    [InlineData("PropertyOwner", OrgRole.Collaborator, "short-rent/staff")]
    [InlineData("LongTermLandlord", OrgRole.Collaborator, "long-rent/staff")]
    [InlineData("PropertyOwner", OrgRole.PropertyManager, "short-rent/property_manager")]
    [InlineData("PropertyOwner", OrgRole.Accountant, "account/org_accountant")]
    public async Task HandleAsync_AMemberWhoIsNotTheOwner_IsNotMadeBillingAdminByAnOwnerRoleLeftInTheToken(
        string tokenRole,
        OrgRole memberRole,
        string membership)
    {
        var handler = CreateHandler(MemberSnapshot(memberRole, membership));
        var context = Context([new Claim(ClaimTypes.Role, tokenRole)]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("https://casazen.app/roles")]
    public async Task HandleAsync_TheLeftOverOwnerRoleIsRefusedInEitherClaimType(string claimType)
    {
        var handler = CreateHandler(MemberSnapshot(OrgRole.Collaborator, "short-rent/staff"));
        var context = Context([new Claim(claimType, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_TheOwnerWithItsMemberRow_StillPassesWithItsTokenRole()
    {
        var handler = CreateHandler(MemberSnapshot(OrgRole.Owner, "account/org_owner"));
        var context = Context([new Claim(ClaimTypes.Role, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_AnAdminMemberWithALeftOverOwnerRole_PassesThroughItsAccountMembershipNotTheToken()
    {
        var handler = CreateHandler(MemberSnapshot(OrgRole.Admin, "account/org_admin"));
        var context = Context([new Claim(ClaimTypes.Role, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_ThePlatformAdminRole_IsNotAnOrgRoleAndAlwaysCounts()
    {
        var handler = CreateHandler(MemberSnapshot(OrgRole.Collaborator, "short-rent/staff"));
        var context = Context([new Claim(ClaimTypes.Role, "Admin")]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_AUserWithNoMemberRow_KeepsTheTokenRoleAsBefore()
    {
        var handler = CreateHandler(Snapshot(UserRole.None, ["short-rent/staff"]));
        var context = Context([new Claim(ClaimTypes.Role, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task HandleAsync_RolesAndMemberships_PassOnlyTheOwnerOrThePlatformAdmin(
        string scenario,
        string[] jwtRoles,
        UserRole dbRole,
        string[] memberships,
        bool allowed)
    {
        var handler = CreateHandler(Snapshot(dbRole, memberships));
        var context = Context(jwtRoles.Select(role => new Claim(ClaimTypes.Role, role)));

        await handler.HandleAsync(context);

        Assert.True(allowed == context.HasSucceeded, $"{scenario}: expected allowed={allowed}, policy {(context.HasSucceeded ? "passed" : "refused")}.");
    }

    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("https://casazen.app/roles")]
    public async Task HandleAsync_PropertyManagerRoleInEitherClaimType_IsRefused(string claimType)
    {
        var handler = CreateHandler(Snapshot(UserRole.PropertyManager, []));
        var context = Context([new Claim(claimType, "PropertyManager")]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("https://casazen.app/roles")]
    public async Task HandleAsync_OwnerRoleInEitherClaimType_Passes(string claimType)
    {
        var handler = CreateHandler(Snapshot(UserRole.PropertyOwner, []));
        var context = Context([new Claim(claimType, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_MemberRefusedByMembership_DoesNotReachOnboardingGateNorOrgResolution()
    {
        // A member is refused before anything is read about the org: no side effect (the org is provisioned on resolve).
        var orgResolver = new Mock<IOrgContextResolver>(MockBehavior.Strict);
        var gate = new Mock<IHostOnboardingGate>(MockBehavior.Strict);
        var handler = CreateHandler(Snapshot(UserRole.None, ["short-rent/staff"]), orgResolver, gate);
        var context = Context([]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.False(context.HasFailed);
        orgResolver.VerifyNoOtherCalls();
        gate.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_OwnerWithoutCompletedOnboarding_FailsWithOnboardingRequired()
    {
        var handler = CreateHandler(Snapshot(UserRole.PropertyOwner, ["short-rent/property_owner"]), onboardingComplete: false);
        var context = Context([new Claim(ClaimTypes.Role, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
        Assert.True(OnboardingRequiredAuthorizationResultHandler.IsOnboardingRequired(
            AuthorizationFailure.Failed(context.FailureReasons)));
    }

    [Fact]
    public async Task HandleAsync_OwnerWhoseOrgCannotBeResolved_IsRefused()
    {
        var orgResolver = new Mock<IOrgContextResolver>();
        orgResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var handler = CreateHandler(Snapshot(UserRole.PropertyOwner, []), orgResolver);
        var context = Context([new Claim(ClaimTypes.Role, "PropertyOwner")]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HandleAsync_OwnerMembershipOfAnInactiveOrMissingUser_IsRefused(bool exists, bool isActive)
    {
        var snapshot = new UserAuthorizationSnapshot(
            Exists: exists,
            IsActive: isActive,
            Role: UserRole.PropertyOwner,
            SupplierOrgId: null,
            Memberships: [Access("short-rent", "property_owner")]);
        var handler = CreateHandler(snapshot);
        var context = Context([]);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_UnauthenticatedCaller_IsRefused()
    {
        var handler = CreateHandler(Snapshot(UserRole.PropertyOwner, ["short-rent/property_owner"]));
        var anonymous = new AuthorizationHandlerContext(
            [new OrgBillingAdminRequirement()], new ClaimsPrincipal(new ClaimsIdentity()), resource: null);

        await handler.HandleAsync(anonymous);

        Assert.False(anonymous.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_CallerWithoutSubject_IsRefused()
    {
        var handler = CreateHandler(Snapshot(UserRole.PropertyOwner, ["short-rent/property_owner"]));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "PropertyOwner")], "TestAuth"));
        var context = new AuthorizationHandlerContext([new OrgBillingAdminRequirement()], principal, resource: null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static object[] Row(string name, string[] jwtRoles, UserRole dbRole, string[] memberships, bool allowed) =>
        [name, jwtRoles, dbRole, memberships, allowed];

    internal static ContextAccess Access(string contextKey, string roleKey) =>
        new(contextKey, contextKey, roleKey, ["property.read"], $"/app/{contextKey}");

    internal static UserAuthorizationSnapshot Snapshot(UserRole dbRole, string[] memberships) => new(
        Exists: true,
        IsActive: true,
        Role: dbRole,
        SupplierOrgId: null,
        Memberships: memberships
            .Select(m => m.Split('/'))
            .Select(parts => Access(parts[0], parts[1]))
            .ToList());

    internal static AuthorizationHandlerContext Context(IEnumerable<Claim> roleClaims)
    {
        var claims = new List<Claim> { new("sub", Sub) };
        claims.AddRange(roleClaims);
        return new AuthorizationHandlerContext(
            [new OrgBillingAdminRequirement()],
            new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
            resource: null);
    }

    internal static OrgBillingAdminAuthorizationHandler CreateHandler(
        UserAuthorizationSnapshot snapshot,
        Mock<IOrgContextResolver>? orgResolver = null,
        Mock<IHostOnboardingGate>? gate = null,
        bool onboardingComplete = true)
    {
        if (orgResolver is null)
        {
            orgResolver = new Mock<IOrgContextResolver>();
            orgResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        }

        if (gate is null)
        {
            gate = new Mock<IHostOnboardingGate>();
            gate.Setup(g => g.GetStatusAsync(Sub, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HostOnboardingStatus(onboardingComplete, onboardingComplete));
        }

        var store = new Mock<IUserAuthorizationSnapshotStore>();
        store.Setup(s => s.GetAsync(Sub, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        return new OrgBillingAdminAuthorizationHandler(orgResolver.Object, store.Object, gate.Object);
    }
}
