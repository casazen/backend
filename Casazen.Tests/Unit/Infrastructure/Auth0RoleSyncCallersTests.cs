using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Callers of the Auth0 role sync: no per-request Management API calls (A4-01), outcome propagated
/// to the client (A1-02), backend authorization independent from JWT roles (A1-02).
/// </summary>
public class Auth0RoleSyncCallersTests
{
    [Fact]
    public async Task GetOrProvisionSupplierOrgIdAsync_AlreadyLinkedSupplier_DoesNotCallAuth0()
    {
        const string sub = "auth0|dual-role";
        var orgId = Guid.NewGuid();
        var user = new User { Id = sub, Email = "dual@test.com", SupplierOrgId = orgId, Role = UserRole.PropertyOwner };
        var userService = new Mock<IUserService>();
        userService.Setup(s => s.GetCurrentUserAsync(sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(user);
        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.GetOrProvisionSupplierOrgIdAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(orgId);
        var auth0 = new Mock<IAuth0ManagementService>(MockBehavior.Strict);
        var cache = new Mock<IUserAuthorizationCache>();
        await using var db = CreateDb();
        var resolver = new SupplierOrgContextResolver(
            Accessor(sub, email: "dual@test.com"),
            userService.Object,
            supplierService.Object,
            db,
            auth0.Object,
            cache.Object);

        var first = await resolver.GetOrProvisionSupplierOrgIdAsync();
        var second = await resolver.GetOrProvisionSupplierOrgIdAsync();

        Assert.Equal(orgId, first);
        Assert.Equal(orgId, second);
        // Strict mock: any Auth0 call (role assignment or profile lookup) would throw.
        auth0.VerifyNoOtherCalls();
        cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOrProvisionSupplierOrgIdAsync_NewLink_InvalidatesAuthorizationCacheWithoutCallingAuth0()
    {
        const string sub = "auth0|new-link";
        var orgId = Guid.NewGuid();
        var user = new User { Id = sub, Email = "link@test.com", SupplierOrgId = null };
        var userService = new Mock<IUserService>();
        userService.Setup(s => s.GetCurrentUserAsync(sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(user);
        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.GetOrProvisionSupplierOrgIdAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(orgId);
        var auth0 = new Mock<IAuth0ManagementService>(MockBehavior.Strict);
        var cache = new Mock<IUserAuthorizationCache>();
        await using var db = CreateDb();
        var resolver = new SupplierOrgContextResolver(
            Accessor(sub, email: "link@test.com"), userService.Object, supplierService.Object, db, auth0.Object, cache.Object);

        await resolver.GetOrProvisionSupplierOrgIdAsync();

        cache.Verify(c => c.Invalidate(sub), Times.Once);
        auth0.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChangeRole_Auth0SyncFails_Returns502WithCode()
    {
        var userService = new Mock<IUserService>();
        userService.Setup(s => s.ChangeRoleAsync("auth0|target", UserRole.Admin, "auth0|admin"))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.RateLimitedCode));
        var controller = CreateUsersController(userService.Object, "auth0|admin");

        var result = await controller.ChangeRole("auth0|target", new Casazen.Web.DTOs.Users.ChangeRoleDto { Role = "Admin" });

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
        Assert.Equal(Auth0SyncResult.RateLimitedCode, problem.Extensions["code"]);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.NotEqual("Auth0RoleSyncFailed", problem.Detail);
    }

    [Fact]
    public async Task ChangeRole_Auth0Synced_Returns200()
    {
        var userService = new Mock<IUserService>();
        userService.Setup(s => s.ChangeRoleAsync("auth0|target", UserRole.LongTermLandlord, "auth0|admin"))
            .ReturnsAsync(Auth0SyncResult.Synced);
        var controller = CreateUsersController(userService.Object, "auth0|admin");

        var result = await controller.ChangeRole("auth0|target", new Casazen.Web.DTOs.Users.ChangeRoleDto { Role = "LongTermLandlord" });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task OrgBillingAdmin_NoJwtRoleButShortRentMembership_Succeeds()
    {
        const string sub = "auth0|billing-host";
        var handler = CreateBillingHandler(sub, new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.PropertyOwner,
            SupplierOrgId: null,
            Memberships: [new ContextAccess("short-rent", "Affitti brevi", "property_owner", ["property.read"], "/app/short-rent")]));
        var context = BillingContext(sub);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task OrgBillingAdmin_NoJwtRoleAndOnlyLongRentMembership_DoesNotSucceed()
    {
        const string sub = "auth0|billing-ltr";
        var handler = CreateBillingHandler(sub, new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.LongTermLandlord,
            SupplierOrgId: null,
            Memberships: [new ContextAccess("long-rent", "Affitti lungo termine", "long_term_landlord", ["lease.read"], "/app/long-rent/leases")]));
        var context = BillingContext(sub);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static OrgBillingAdminAuthorizationHandler CreateBillingHandler(string sub, UserAuthorizationSnapshot snapshot)
    {
        var orgResolver = new Mock<IOrgContextResolver>();
        orgResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var store = new Mock<IUserAuthorizationSnapshotStore>();
        store.Setup(s => s.GetAsync(sub, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        return new OrgBillingAdminAuthorizationHandler(orgResolver.Object, store.Object);
    }

    private static AuthorizationHandlerContext BillingContext(string sub)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "TestAuth"));
        return new AuthorizationHandlerContext([new OrgBillingAdminRequirement()], principal, resource: null);
    }

    private static UsersController CreateUsersController(IUserService userService, string adminSub)
    {
        var controller = new UsersController(
            userService,
            Mock.Of<IOrgService>(),
            Mock.Of<IOnboardingService>(),
            NullLogger<UsersController>.Instance,
            Mock.Of<IEntitlementService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("sub", adminSub), new Claim(ClaimTypes.Role, "Admin")], "TestAuth")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
        return controller;
    }

    private static HttpContextAccessor Accessor(string sub, string email) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", sub), new Claim("email", email), new Claim(ClaimTypes.Role, "Supplier")], "TestAuth")),
        },
    };

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
