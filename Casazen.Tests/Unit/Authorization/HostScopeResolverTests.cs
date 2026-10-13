using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-03 (S3): the one answer to «which properties does the caller reach?», read from the org membership in the
/// authorization snapshot and not from the token. A role × scope × status table, then the single-resource check and the cache.
/// </summary>
public class HostScopeResolverTests
{
    private const string UserId = "auth0|member";
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PropertyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly IReadOnlySet<string> NoRoles = new HashSet<string>();

    private static UserAuthorizationSnapshot Snapshot(
        OrgRole? role = null,
        PropertyScope scope = PropertyScope.All,
        OrgMemberStatus status = OrgMemberStatus.Active,
        Guid? memberOrg = null,
        bool exists = true,
        bool active = true,
        params Guid[] granted) => new(
        Exists: exists,
        IsActive: active,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: role is null
            ? null
            : new OrgMemberSnapshot(memberOrg ?? OrgId, role.Value, status, scope, granted.Length == 0 ? null : granted.ToHashSet()));

    private static HashSet<string> Roles(params string[] roles) => new(roles, StringComparer.Ordinal);

    // ─── a member of the team: the row decides ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public void Decide_OwnerAdminManagerAndAccountant_ReachEveryPropertyOfTheOrg(OrgRole role)
    {
        var scope = HostScopeResolver.Decide(Snapshot(role), UserId, NoRoles, OrgId);

        Assert.Equal(new HostScope(OrgId), scope);
        Assert.True(scope!.IsOrgWide);
    }

    [Theory]
    [InlineData(OrgRole.Owner, PropertyScope.Selected)]
    [InlineData(OrgRole.Admin, PropertyScope.Selected)]
    [InlineData(OrgRole.PropertyManager, PropertyScope.Selected)]
    [InlineData(OrgRole.Accountant, PropertyScope.Selected)]
    public void Decide_ASelectedScopeOnARoleThatIsNotTheCollaborators_IsIgnored_TheRoleReachesTheWholeOrg(OrgRole role, PropertyScope stored)
    {
        // «Solo alcuni» is the collaborator's. A stale value on another role never narrows, and the writes refuse to set it.
        Assert.True(HostScopeResolver.Decide(Snapshot(role, stored), UserId, NoRoles, OrgId)!.IsOrgWide);
    }

    [Fact]
    public void Decide_CollaboratorWithEveryProperty_ReachesTheWholeOrg() =>
        Assert.True(HostScopeResolver.Decide(Snapshot(OrgRole.Collaborator, PropertyScope.All), UserId, NoRoles, OrgId)!.IsOrgWide);

    [Fact]
    public void Decide_CollaboratorSoloAlcuni_IsGrantedToItsOwnUserId()
    {
        var scope = HostScopeResolver.Decide(Snapshot(OrgRole.Collaborator, PropertyScope.Selected), UserId, NoRoles, OrgId);

        Assert.Equal(new HostScope(OrgId, GrantedToUserId: UserId), scope);
        Assert.False(scope!.IsOrgWide);
    }

    [Fact]
    public void Decide_ATokenRoleNeverWidensAMember()
    {
        // A role left in the token (a former property manager, a PropertyOwner of an org it left, a platform Admin) does not
        // widen a collaborator «Solo alcuni».
        var scope = HostScopeResolver.Decide(
            Snapshot(OrgRole.Collaborator, PropertyScope.Selected), UserId, Roles("PropertyManager", "Admin", "PropertyOwner"), OrgId);

        Assert.Equal(new HostScope(OrgId, GrantedToUserId: UserId), scope);
    }

    [Fact]
    public void Decide_ADeactivatedMember_HasNoScope_WhateverItsRoleOrToken() =>
        Assert.Null(HostScopeResolver.Decide(
            Snapshot(OrgRole.Owner, status: OrgMemberStatus.Deactivated), UserId, Roles("PropertyManager"), OrgId));

    [Fact]
    public void Decide_AMemberOfAnotherOrg_HasNoScopeHere() =>
        Assert.Null(HostScopeResolver.Decide(Snapshot(OrgRole.Owner, memberOrg: Guid.NewGuid()), UserId, NoRoles, OrgId));

    [Fact]
    public void Decide_AnInactiveAccount_HasNoScope() =>
        Assert.Null(HostScopeResolver.Decide(Snapshot(OrgRole.Owner, active: false), UserId, NoRoles, OrgId));

    // ─── an account in no org team: the rule of before the team ───────────────────────────────────────

    [Theory]
    [InlineData("PropertyManager")]
    [InlineData("Admin")]
    public void Decide_NoMembership_AnOrgWideTokenRole_ReachesTheWholeOrg(string role) =>
        Assert.True(HostScopeResolver.Decide(Snapshot(), UserId, Roles(role), OrgId)!.IsOrgWide);

    [Theory]
    [InlineData("PropertyOwner")]
    [InlineData("LongTermLandlord")]
    public void Decide_NoMembership_AnyOtherTokenRole_ReachesTheOnesItCreated(string role) =>
        Assert.Equal(new HostScope(OrgId, OwnerId: UserId), HostScopeResolver.Decide(Snapshot(), UserId, Roles(role), OrgId));

    [Fact]
    public void Decide_AnAccountTheDatabaseDoesNotKnow_IsDecidedByItsToken() =>
        Assert.Equal(
            new HostScope(OrgId, OwnerId: UserId),
            HostScopeResolver.Decide(UserAuthorizationSnapshot.Missing, UserId, Roles("PropertyOwner"), OrgId));

    [Fact]
    public void Decide_NoMembershipAndNoRole_ReachesOnlyTheOnesItCreated() =>
        Assert.Equal(new HostScope(OrgId, OwnerId: UserId), HostScopeResolver.Decide(Snapshot(), UserId, NoRoles, OrgId));

    // ─── the single-resource check ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.PropertyManager)]
    public void Reaches_OrgWide_IsTrueWhateverThePropertyIs(OrgRole role)
    {
        var snapshot = Snapshot(role);
        var scope = HostScopeResolver.Decide(snapshot, UserId, NoRoles, OrgId)!;

        Assert.True(HostScopeResolver.Reaches(snapshot, scope, PropertyA, "auth0|creator"));
        Assert.True(HostScopeResolver.Reaches(snapshot, scope, PropertyB, "auth0|creator"));
        Assert.True(HostScopeResolver.Reaches(snapshot, scope, null, null));
    }

    [Fact]
    public void Reaches_SoloAlcuni_IsTheGivenPropertiesOnly_AndFailsClosedWithoutTheId()
    {
        var snapshot = Snapshot(OrgRole.Collaborator, PropertyScope.Selected, granted: PropertyA);
        var scope = HostScopeResolver.Decide(snapshot, UserId, NoRoles, OrgId)!;

        Assert.True(HostScopeResolver.Reaches(snapshot, scope, PropertyA, "auth0|creator"));
        Assert.False(HostScopeResolver.Reaches(snapshot, scope, PropertyB, "auth0|creator"));
        // The creator being the member itself changes nothing: a restricted collaborator is not an owner.
        Assert.False(HostScopeResolver.Reaches(snapshot, scope, PropertyB, UserId));
        Assert.False(HostScopeResolver.Reaches(snapshot, scope, null, "auth0|creator"));
    }

    [Fact]
    public void Reaches_SoloAlcuniGivenNothing_ReachesNothing()
    {
        var snapshot = Snapshot(OrgRole.Collaborator, PropertyScope.Selected);
        var scope = HostScopeResolver.Decide(snapshot, UserId, NoRoles, OrgId)!;

        Assert.False(HostScopeResolver.Reaches(snapshot, scope, PropertyA, "auth0|creator"));
    }

    [Fact]
    public void Reaches_AnAccountInNoTeam_IsTheCreatorOfTheProperty()
    {
        var snapshot = Snapshot();
        var scope = HostScopeResolver.Decide(snapshot, UserId, Roles("PropertyOwner"), OrgId)!;

        Assert.True(HostScopeResolver.Reaches(snapshot, scope, PropertyA, UserId));
        Assert.False(HostScopeResolver.Reaches(snapshot, scope, PropertyA, "auth0|somebody-else"));
        Assert.False(HostScopeResolver.Reaches(snapshot, scope, PropertyA, null));
    }

    // ─── through the store: one snapshot read, and no user id means no scope ───────────────────────────

    [Fact]
    public async Task ResolveAsync_ReadsTheSnapshotOfTheUser_AndNeverForAnAnonymousCaller()
    {
        var store = new Mock<IUserAuthorizationSnapshotStore>();
        store.Setup(s => s.GetAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(OrgRole.Collaborator, PropertyScope.Selected, granted: PropertyA));
        var resolver = new HostScopeResolver(store.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<HostScopeResolver>.Instance);

        var scope = await resolver.ResolveAsync(UserId, NoRoles, OrgId);
        var reaches = await resolver.CanReachPropertyAsync(UserId, NoRoles, OrgId, PropertyA, "auth0|creator");
        var anonymous = await resolver.ResolveAsync(" ", NoRoles, OrgId);
        var anonymousReaches = await resolver.CanReachPropertyAsync("", NoRoles, OrgId, PropertyA, "auth0|creator");

        Assert.Equal(new HostScope(OrgId, GrantedToUserId: UserId), scope);
        Assert.True(reaches);
        Assert.Null(anonymous);
        Assert.False(anonymousReaches);
        store.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
