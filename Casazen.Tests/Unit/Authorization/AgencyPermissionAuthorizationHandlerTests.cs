using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// DEB-01 (2026-10-31): the agency policies checked the plan only, so any member of an agency org read the full IBAN of the owners and
/// created wire batches. Each policy now also needs its agency permission, held by the caller's org role
/// (<see cref="OrgRoleCatalog.AgencyPermissionsOf"/>, docs/runbooks/org-team.md § 57).
/// </summary>
public class AgencyPermissionAuthorizationHandlerTests
{
    private const string Sub = "auth0|agency-permission-test";
    private static readonly Guid OrgId = Guid.NewGuid();

    /// <summary>The product rule, by hand: who may read the full IBAN, pay the owners, see the list, run the statements and the withholding.</summary>
    [Theory]
    [InlineData(OrgRole.Owner, AgencyPermissions.OwnerFinancialRead, true)]
    [InlineData(OrgRole.Owner, AgencyPermissions.WireManage, true)]
    [InlineData(OrgRole.Admin, AgencyPermissions.OwnerFinancialRead, true)]
    [InlineData(OrgRole.Admin, AgencyPermissions.WireManage, true)]
    [InlineData(OrgRole.Admin, AgencyPermissions.StatementManage, true)]
    [InlineData(OrgRole.PropertyManager, AgencyPermissions.OwnerView, true)]
    [InlineData(OrgRole.PropertyManager, AgencyPermissions.OwnerFinancialRead, false)]
    [InlineData(OrgRole.PropertyManager, AgencyPermissions.WireManage, false)]
    [InlineData(OrgRole.PropertyManager, AgencyPermissions.StatementView, false)]
    [InlineData(OrgRole.PropertyManager, AgencyPermissions.WithholdingManage, false)]
    [InlineData(OrgRole.Collaborator, AgencyPermissions.OwnerView, false)]
    [InlineData(OrgRole.Collaborator, AgencyPermissions.OwnerFinancialRead, false)]
    [InlineData(OrgRole.Collaborator, AgencyPermissions.WireManage, false)]
    [InlineData(OrgRole.Accountant, AgencyPermissions.OwnerFinancialRead, false)]
    [InlineData(OrgRole.Accountant, AgencyPermissions.WireManage, false)]
    [InlineData(OrgRole.Accountant, AgencyPermissions.StatementView, false)]
    public async Task Member_PassesOnlyWithThePermissionOfItsRole(OrgRole role, string permission, bool expected)
    {
        var context = await HandleAsync(Member(role), permission);

        Assert.Equal(expected, context.HasSucceeded);
    }

    [Fact]
    public async Task OwnerAndAdmin_HoldEveryAgencyPermission_CollaboratorAndAccountantNone()
    {
        foreach (var permission in AgencyPermissions.All)
        {
            Assert.True((await HandleAsync(Member(OrgRole.Owner), permission)).HasSucceeded, permission);
            Assert.True((await HandleAsync(Member(OrgRole.Admin), permission)).HasSucceeded, permission);
            Assert.False((await HandleAsync(Member(OrgRole.Collaborator), permission)).HasSucceeded, permission);
            Assert.False((await HandleAsync(Member(OrgRole.Accountant), permission)).HasSucceeded, permission);
        }
    }

    [Fact]
    public async Task Owner_OfAnotherOrg_DeactivatedMember_OrInactiveAccount_HoldsNothing()
    {
        var otherOrg = Member(OrgRole.Owner) with { OrgMember = new OrgMemberSnapshot(Guid.NewGuid(), OrgRole.Owner, OrgMemberStatus.Active) };
        var deactivated = Member(OrgRole.Admin) with { OrgMember = new OrgMemberSnapshot(OrgId, OrgRole.Admin, OrgMemberStatus.Deactivated) };
        var inactive = Member(OrgRole.Owner) with { IsActive = false };

        Assert.False((await HandleAsync(otherOrg, AgencyPermissions.OwnerFinancialRead)).HasSucceeded);
        Assert.False((await HandleAsync(deactivated, AgencyPermissions.OwnerFinancialRead)).HasSucceeded);
        Assert.False((await HandleAsync(inactive, AgencyPermissions.OwnerFinancialRead)).HasSucceeded);
    }

    [Fact]
    public async Task Member_IsDecidedByItsRow_NotByAnOwnerRoleLeftInTheToken()
    {
        var context = await HandleAsync(Member(OrgRole.Collaborator), AgencyPermissions.OwnerFinancialRead, "PropertyOwner");

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task UserInNoOrgTeam_IsTheOwnerOnlyWithTheOwnersTokenRoleOrMembership()
    {
        var legacy = Snapshot(orgMember: null);
        var legacyWithOwnerRow = Snapshot(orgMember: null, memberships:
            [new ContextAccess(OrgRoleCatalog.ShortRent, "Affitti brevi", OrgRoleCatalog.ShortRentOwnerRoleKey, ["property.read"], "/app/short-rent")]);

        Assert.True((await HandleAsync(legacy, AgencyPermissions.OwnerFinancialRead, "PropertyOwner")).HasSucceeded);
        Assert.True((await HandleAsync(legacyWithOwnerRow, AgencyPermissions.WireManage)).HasSucceeded);
        Assert.False((await HandleAsync(legacy, AgencyPermissions.OwnerFinancialRead, "PropertyManager")).HasSucceeded);
        Assert.False((await HandleAsync(legacy, AgencyPermissions.OwnerFinancialRead, "PropertyOwner", "Staff")).HasSucceeded);
    }

    [Fact]
    public async Task OnboardingNotComplete_FailsWithOnboardingRequired()
    {
        var context = await HandleAsync(Member(OrgRole.Owner), AgencyPermissions.OwnerView, onboarded: false);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r.Message == HostOnboarding.RequiredCode);
    }

    public static TheoryData<string> AgencyPolicyNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in CasazenPolicies.AgencyPolicies)
                names.Add(name);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(AgencyPolicyNames))]
    public async Task EveryAgencyPolicy_RequiresTheTierAndItsOwnPermission(string policyName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        var provider = services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(policyName);

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is AgencyTierRequirement);
        var permission = Assert.Single(policy.Requirements.OfType<AgencyPermissionRequirement>());
        Assert.Equal(CasazenPolicies.ParseAgencyPolicy(policyName), permission.PermissionKey);
        Assert.Contains(permission.PermissionKey, AgencyPermissions.All);
    }

    private static UserAuthorizationSnapshot Member(OrgRole role) =>
        Snapshot(new OrgMemberSnapshot(OrgId, role, OrgMemberStatus.Active));

    private static UserAuthorizationSnapshot Snapshot(OrgMemberSnapshot? orgMember, IReadOnlyList<ContextAccess>? memberships = null) =>
        new(Exists: true, IsActive: true, Role: UserRole.None, SupplierOrgId: null, Memberships: memberships ?? [], OrgId: OrgId,
            OrgMember: orgMember);

    private static async Task<AuthorizationHandlerContext> HandleAsync(
        UserAuthorizationSnapshot snapshot,
        string permission,
        params string[] tokenRoles) =>
        await HandleAsync(snapshot, permission, onboarded: true, tokenRoles);

    private static async Task<AuthorizationHandlerContext> HandleAsync(
        UserAuthorizationSnapshot snapshot,
        string permission,
        bool onboarded,
        params string[] tokenRoles)
    {
        var snapshots = new Mock<IUserAuthorizationSnapshotStore>();
        snapshots.Setup(s => s.GetAsync(Sub, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var onboarding = new Mock<IHostOnboardingGate>();
        onboarding.Setup(g => g.GetStatusAsync(Sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostOnboardingStatus(onboarded, onboarded));

        var handler = new AgencyPermissionAuthorizationHandler(
            snapshots.Object, orgs.Object, onboarding.Object, NullLogger<AgencyPermissionAuthorizationHandler>.Instance);

        var claims = new List<Claim> { new("sub", Sub) };
        claims.AddRange(tokenRoles.Select(role => new Claim(ClaimTypes.Role, role)));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        var context = new AuthorizationHandlerContext([new AgencyPermissionRequirement(permission)], user, null);

        await handler.HandleAsync(context);
        return context;
    }
}
