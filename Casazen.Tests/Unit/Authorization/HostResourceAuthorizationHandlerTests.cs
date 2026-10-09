using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// TN-3: the resource handler grants an operation on a host row only for the caller's org, with the context
/// permission, and on property-bound rows only to the people who reach that property: the org role of a member (AM-03: every
/// property, or the ones a collaborator «Solo alcuni» was given), or, for an account in no org team, its creator or an
/// org-wide token role.
/// </summary>
public class HostResourceAuthorizationHandlerTests
{
    private const string OwnerId = "auth0|owner";
    private static readonly Guid OrgId = Guid.NewGuid();

    [Fact]
    public async Task Authorize_OwnerOfPropertyInOwnOrgWithPermission_Succeeds()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId), new HostResource(OrgId, OwnerId), PropertyOperations.Write);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_RowOfAnotherOrg_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId, "Admin"),
            new HostResource(Guid.NewGuid(), OwnerId),
            PropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_CallerWithoutOrg_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(callerOrgId: null);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId), new HostResource(OrgId, OwnerId), PropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_MissingContextPermission_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(
            OrgId, (_, permission) => permission != "payment.write");

        var denied = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId), new HostResource(OrgId, OwnerId), PaymentOperations.Write);
        var allowed = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId), new HostResource(OrgId, OwnerId), PaymentOperations.Read);

        Assert.False(denied.Succeeded);
        Assert.True(allowed.Succeeded);
    }

    [Fact]
    public async Task Authorize_SameOrgUserNotOwningProperty_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|colleague", "PropertyOwner"),
            new HostResource(OrgId, OwnerId),
            BookingOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("PropertyManager")]
    [InlineData("Admin")]
    public async Task Authorize_SameOrgOrgWideRoleNotOwningProperty_Succeeds(string role)
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|manager", role),
            new HostResource(OrgId, OwnerId),
            PropertyOperations.Write);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_OrgLevelResource_DoesNotRequireOwnership()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|colleague"), HostResource.ForOrg(OrgId), GuestOperations.Write);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_AnonymousCaller_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), HostResource.ForOrg(OrgId), GuestOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_OperationOnResourceOfAnotherType_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);
        var property = new Property { OrgId = OrgId, OwnerId = OwnerId };

        // Only HostResource is handled: passing the entity itself never authorizes (fail closed).
        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId), property, PropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ForProperty_UsesOrgOwnerAndIdOfTheProperty()
    {
        var property = new Property { OrgId = OrgId, OwnerId = OwnerId };

        Assert.Equal(new HostResource(OrgId, OwnerId, property.Id), HostResource.ForProperty(property));
        Assert.True(HostResource.ForProperty(property).IsBoundToProperty);
        Assert.False(HostResource.ForOrg(OrgId).IsBoundToProperty);
    }

    [Fact]
    public async Task ResolveHostScope_AccountInNoOrgTeam_IsDecidedByTheToken()
    {
        var resolver = HostAuthorizationTestHarness.ScopeResolver();
        var owner = HostAuthorizationTestHarness.User(OwnerId, "PropertyOwner");
        var manager = HostAuthorizationTestHarness.User("auth0|manager", "PropertyManager");
        var noId = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "PropertyOwner")], "TestAuth"));

        Assert.Equal(new HostScope(OrgId, OwnerId), await resolver.ResolveHostScopeAsync(owner, OrgId));
        Assert.Equal(new HostScope(OrgId), await resolver.ResolveHostScopeAsync(manager, OrgId));
        Assert.Null(await resolver.ResolveHostScopeAsync(noId, OrgId));
    }

    [Fact]
    public async Task ResolveHostScope_ReadsTheAuth0RolesClaimToo()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "auth0|x"), new Claim(ClaimsPrincipalExtensions.Auth0RolesClaim, "[\"PropertyManager\"]")],
            "TestAuth"));

        Assert.True((await HostAuthorizationTestHarness.ScopeResolver().ResolveHostScopeAsync(user, OrgId))!.IsOrgWide);
    }

    // ─── AM-03: the reach comes from the org membership, not from the token ────────────────────────────

    private static readonly Guid GrantedPropertyId = Guid.NewGuid();
    private static readonly Guid OtherPropertyId = Guid.NewGuid();

    private static Casazen.Infrastructure.Services.UserAuthorizationSnapshot Member(
        OrgRole role,
        PropertyScope scope = PropertyScope.All,
        OrgMemberStatus status = OrgMemberStatus.Active,
        params Guid[] granted) => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new Casazen.Infrastructure.Services.OrgMemberSnapshot(
            OrgId, role, status, scope, granted.Length == 0 ? null : granted.ToHashSet()));

    private static IAuthorizationService AuthorizationFor(Casazen.Infrastructure.Services.UserAuthorizationSnapshot snapshot) =>
        HostAuthorizationTestHarness.Create(
            OrgId, scopeResolver: HostAuthorizationTestHarness.ScopeResolver(snapshot));

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task Authorize_MemberOfTheTeamWithAnOrgWideRole_ReachesAPropertyItDidNotCreate_WithoutAnyTokenRole(OrgRole role)
    {
        var authorization = AuthorizationFor(Member(role));

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|member"),
            new HostResource(OrgId, OwnerId, GrantedPropertyId),
            BookingOperations.Read);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_CollaboratorWithEveryProperty_ReachesAnyPropertyOfTheOrg()
    {
        var authorization = AuthorizationFor(Member(OrgRole.Collaborator, PropertyScope.All));

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|collab"),
            new HostResource(OrgId, OwnerId, OtherPropertyId),
            BookingOperations.Read);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_CollaboratorSoloAlcuni_ReachesOnlyTheGivenProperties()
    {
        var authorization = AuthorizationFor(Member(OrgRole.Collaborator, PropertyScope.Selected, granted: GrantedPropertyId));
        var user = HostAuthorizationTestHarness.User("auth0|collab");

        var granted = await authorization.AuthorizeAsync(user, new HostResource(OrgId, OwnerId, GrantedPropertyId), BookingOperations.Read);
        var other = await authorization.AuthorizeAsync(user, new HostResource(OrgId, OwnerId, OtherPropertyId), BookingOperations.Read);

        Assert.True(granted.Succeeded);
        Assert.False(other.Succeeded);
    }

    [Fact]
    public async Task Authorize_CollaboratorSoloAlcuniGivenNothing_ReachesNoProperty()
    {
        var authorization = AuthorizationFor(Member(OrgRole.Collaborator, PropertyScope.Selected));

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|collab"),
            new HostResource(OrgId, OwnerId, GrantedPropertyId),
            BookingOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_CollaboratorSoloAlcuni_AResourceWhosePropertyIsUnknown_FailsClosed()
    {
        var authorization = AuthorizationFor(Member(OrgRole.Collaborator, PropertyScope.Selected, granted: GrantedPropertyId));

        // Bound to a property by its owner only: the id that decides is missing, so the row is not granted.
        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|collab"), new HostResource(OrgId, OwnerId), BookingOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_CollaboratorSoloAlcuni_AnOrgLevelRowIsNotBoundToAProperty()
    {
        var authorization = AuthorizationFor(Member(OrgRole.Collaborator, PropertyScope.Selected));

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|collab"), HostResource.ForOrg(OrgId), GuestOperations.Read);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_DeactivatedMember_ReachesNothing_EvenWithAnOrgWideRoleInTheToken()
    {
        var authorization = AuthorizationFor(Member(OrgRole.PropertyManager, status: OrgMemberStatus.Deactivated));

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|manager", "PropertyManager", "Admin"),
            new HostResource(OrgId, OwnerId, GrantedPropertyId),
            PropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_CollaboratorWithALeftoverOrgWideRoleInTheToken_IsStillLimitedByItsMembership()
    {
        // The row decides: a role left in the token (an old PropertyManager, a PropertyOwner) never widens a collaborator.
        var authorization = AuthorizationFor(Member(OrgRole.Collaborator, PropertyScope.Selected, granted: GrantedPropertyId));

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|collab", "PropertyManager", "Admin", "PropertyOwner"),
            new HostResource(OrgId, OwnerId, OtherPropertyId),
            BookingOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_MemberOfAnotherOrg_ReachesNothingHere()
    {
        var snapshot = Member(OrgRole.Owner) with
        {
            OrgMember = new Casazen.Infrastructure.Services.OrgMemberSnapshot(Guid.NewGuid(), OrgRole.Owner, OrgMemberStatus.Active),
        };
        var authorization = AuthorizationFor(snapshot);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|owner2"),
            new HostResource(OrgId, OwnerId, GrantedPropertyId),
            PropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_InactiveAccount_ReachesNothing()
    {
        var snapshot = Member(OrgRole.Owner) with { IsActive = false };
        var authorization = AuthorizationFor(snapshot);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|owner2", "PropertyOwner"),
            new HostResource(OrgId, "auth0|owner2", GrantedPropertyId),
            PropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_SharedPropertyOperation_WithLongRentPermissionOnly_Succeeds()
    {
        // A long-term landlord holds property.* in long-rent only (A7-06, LT-05).
        var authorization = HostAuthorizationTestHarness.Create(OrgId, (context, _) => context == "long-rent");
        var user = HostAuthorizationTestHarness.User(OwnerId);
        var property = new HostResource(OrgId, OwnerId);

        Assert.True((await authorization.AuthorizeAsync(user, property, SharedPropertyOperations.Read)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(user, property, SharedPropertyOperations.Write)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(user, property, PropertyOperations.Read)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(user, property, PropertyOperations.Write)).Succeeded);
    }

    [Fact]
    public async Task Authorize_SharedPropertyOperation_WithoutPropertyPermissionInAnyContext_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId, (_, permission) => permission != "property.write");

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User(OwnerId), new HostResource(OrgId, OwnerId), SharedPropertyOperations.Write);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Authorize_SharedPropertyOperation_SameOrgUserNotOwningProperty_Fails()
    {
        var authorization = HostAuthorizationTestHarness.Create(OrgId);

        var result = await authorization.AuthorizeAsync(
            HostAuthorizationTestHarness.User("auth0|colleague"), new HostResource(OrgId, OwnerId), SharedPropertyOperations.Read);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void HostOperationRequirement_IsNotTheContextPolicyRequirement()
    {
        // A context-policy handler must never see a resource operation: it would grant on the permission alone.
        Assert.False(typeof(Casazen.Web.Infrastructure.ContextPermissionRequirement)
            .IsAssignableFrom(typeof(HostOperationRequirement)));
        Assert.IsAssignableFrom<IAuthorizationRequirement>(PropertyOperations.Read);
    }
}
