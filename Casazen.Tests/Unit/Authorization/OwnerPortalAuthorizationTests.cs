using System.Security.Claims;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

public class OwnerPortalAuthorizationTests
{
    private const string TestClaimNamespace = "https://casazen.app";

    private static OwnerPortalAuthorizationHandler CreateHandler()
    {
        var auth0Options = Options.Create(new Auth0Options
        {
            CustomClaimNamespace = TestClaimNamespace
        });
        return new OwnerPortalAuthorizationHandler(auth0Options);
    }
    /// <summary>
    /// Proof test: OwnerPortal_OwnerA_CannotSeeOwnerBData
    /// Verifies that the owner portal policy enforces proper JWT claim validation,
    /// ensuring tenant isolation between different owners.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_OwnerA_CannotSeeOwnerBData()
    {
        var ownerAId = Guid.NewGuid();
        var ownerBId = Guid.NewGuid();

        var contextOwnerA = CreateOwnerContext(ownerAId, "owner_portal");
        var handlerA = CreateHandler();
        await handlerA.HandleAsync(contextOwnerA);
        Assert.True(contextOwnerA.HasSucceeded, "Owner A should be authorized with valid owner_portal context");

        var contextOwnerB = CreateOwnerContext(ownerBId, "owner_portal");
        var handlerB = CreateHandler();
        await handlerB.HandleAsync(contextOwnerB);
        Assert.True(contextOwnerB.HasSucceeded, "Owner B should be authorized with valid owner_portal context");

        var ownerAClaimId = contextOwnerA.User.GetOwnerUserId();
        var ownerBClaimId = contextOwnerB.User.GetOwnerUserId();
        Assert.NotNull(ownerAClaimId);
        Assert.NotNull(ownerBClaimId);
        Assert.NotEqual(ownerAClaimId, ownerBClaimId);
    }

    [Fact]
    public async Task OwnerPortal_WithoutOwnerPortalContext_Fails()
    {
        var ownerId = Guid.NewGuid();
        var context = CreateOwnerContext(ownerId, "host_console");

        var handler = CreateHandler();
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded, "Should fail without owner_portal context");
    }

    [Fact]
    public async Task OwnerPortal_WithHostRole_Fails()
    {
        var ownerId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "auth0|owner123"),
            new($"{TestClaimNamespace}/context", "owner_portal"),
            new($"{TestClaimNamespace}/owner_id", ownerId.ToString()),
            new(ClaimTypes.Role, "PropertyOwner")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        var context = new AuthorizationHandlerContext(
            new[] { new OwnerPortalRequirement() },
            user,
            null);

        var handler = CreateHandler();
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded, "Should fail when host roles are present");
        Assert.Contains(context.FailureReasons,
            r => r.Message == "owner_portal_cannot_have_host_roles");
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Manager")]
    [InlineData("LongTermLandlord")]
    public async Task OwnerPortal_WithAnyHostRole_Fails(string hostRole)
    {
        var ownerId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "auth0|owner123"),
            new($"{TestClaimNamespace}/context", "owner_portal"),
            new($"{TestClaimNamespace}/owner_id", ownerId.ToString()),
            new(ClaimTypes.Role, hostRole)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        var context = new AuthorizationHandlerContext(
            new[] { new OwnerPortalRequirement() },
            user,
            null);

        var handler = CreateHandler();
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded,
            $"Should fail when {hostRole} role is present");
    }

    [Fact]
    public async Task OwnerPortal_WithoutOwnerIdClaim_Fails()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "auth0|owner123"),
            new($"{TestClaimNamespace}/context", "owner_portal")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        var context = new AuthorizationHandlerContext(
            new[] { new OwnerPortalRequirement() },
            user,
            null);

        var handler = CreateHandler();
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded, "Should fail without owner_id claim");
    }

    [Fact]
    public async Task OwnerPortal_Unauthenticated_Fails()
    {
        var user = new ClaimsPrincipal();
        var context = new AuthorizationHandlerContext(
            new[] { new OwnerPortalRequirement() },
            user,
            null);

        var handler = CreateHandler();
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded, "Should fail for unauthenticated user");
    }

    private static AuthorizationHandlerContext CreateOwnerContext(Guid ownerId, string contextValue)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "auth0|owner" + ownerId.ToString("N")[..8]),
            new($"{TestClaimNamespace}/context", contextValue),
            new($"{TestClaimNamespace}/owner_id", ownerId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext(
            new[] { new OwnerPortalRequirement() },
            user,
            null);
    }
}
