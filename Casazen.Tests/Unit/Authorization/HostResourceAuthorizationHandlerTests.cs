using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// TN-3: the resource handler grants an operation on a host row only for the caller's org, with the context
/// permission, and on property-bound rows only to the owner or an org-wide role.
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
    public void ForProperty_UsesOrgAndOwnerOfTheProperty()
    {
        var property = new Property { OrgId = OrgId, OwnerId = OwnerId };

        Assert.Equal(new HostResource(OrgId, OwnerId), HostResource.ForProperty(property));
    }

    [Fact]
    public void GetHostScope_WithoutOrgWideRole_IsBoundToTheCaller()
    {
        var owner = HostAuthorizationTestHarness.User(OwnerId, "PropertyOwner");
        var manager = HostAuthorizationTestHarness.User("auth0|manager", "PropertyManager");
        var noId = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "PropertyOwner")], "TestAuth"));

        Assert.Equal(new HostScope(OrgId, OwnerId), owner.GetHostScope(OrgId));
        Assert.Equal(new HostScope(OrgId, null), manager.GetHostScope(OrgId));
        Assert.Null(noId.GetHostScope(OrgId));
    }

    [Fact]
    public void GetRoles_ReadsTheAuth0RolesClaimToo()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "auth0|x"), new Claim(ClaimsPrincipalExtensions.Auth0RolesClaim, "[\"PropertyManager\"]")],
            "TestAuth"));

        Assert.True(user.HasOrgWideHostAccess());
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
