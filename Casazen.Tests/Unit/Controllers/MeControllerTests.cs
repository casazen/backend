using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Auth;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class MeControllerTests
{
    [Fact]
    public async Task GetContexts_WhenUserInactive_Returns403()
    {
        var userService = new Mock<IUserService>();
        var contextService = new Mock<IContextAuthorizationService>();
        userService.Setup(x => x.GetCurrentUserAsync("auth0|inactive", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User { Id = "auth0|inactive", Email = "user@test.com", FirstName = "User", LastName = "Test", IsActive = false });

        var controller = BuildController(userService.Object, contextService.Object, "auth0|inactive");

        var result = await controller.GetContexts(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        // Same stable code as InactiveAccountMiddleware (PL-03), so the web app opens the "account disabled" page.
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ProblemCodes.AccountInactive, problem.Extensions["code"]);
    }

    [Fact]
    public async Task GetContexts_WhenUserActive_ReturnsBootstrapResponse()
    {
        var userService = new Mock<IUserService>();
        var contextService = new Mock<IContextAuthorizationService>();
        userService.Setup(x => x.GetCurrentUserAsync("auth0|active", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User
            {
                Id = "auth0|active",
                Email = "user@test.com",
                FirstName = "User",
                LastName = "Test",
                IsActive = true,
                LastUsedContextKey = "short-rent",
            });
        contextService.Setup(x => x.GetUserContextsAsync("auth0|active", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ContextAccess("short-rent", "Affitti brevi", "property_owner", ["booking.read"], "/app/short-rent"),
            ]);

        var controller = BuildController(userService.Object, contextService.Object, "auth0|active");

        var result = await controller.GetContexts(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<UserContextsResponse>(ok.Value);
        Assert.Equal("auth0|active", payload.UserId);
        Assert.Single(payload.Contexts);
        Assert.Equal("short-rent", payload.LastUsedContextKey);
    }

    [Theory]
    [InlineData(false, new[] { "long-rent", "short-rent" })]
    [InlineData(true, new[] { "account", "long-rent", "short-rent" })]
    public async Task GetContexts_AccountContext_IsListedOnlyWithTheOrgTeamFlag(bool orgTeam, string[] expectedContexts)
    {
        // AM-01: the clients of today do not know the account context (the web workspace switcher has no icon for it and
        // the first context of the list becomes the active one): it stays out of the list until the flag is on.
        var userService = new Mock<IUserService>();
        var contextService = new Mock<IContextAuthorizationService>();
        userService.Setup(x => x.GetCurrentUserAsync("auth0|owner", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User { Id = "auth0|owner", Email = "o@test.com", FirstName = "O", LastName = "Wner", IsActive = true });
        contextService.Setup(x => x.GetUserContextsAsync("auth0|owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ContextAccess("account", "Amministrazione", "org_owner", ["org.billing.manage"], "/app/account"),
                new ContextAccess("long-rent", "Affitti lungo termine", "long_term_landlord", ["lease.read"], "/app/long-rent/leases"),
                new ContextAccess("short-rent", "Affitti brevi", "property_owner", ["booking.read"], "/app/short-rent"),
            ]);

        var controller = BuildController(userService.Object, contextService.Object, "auth0|owner", orgTeam);

        var result = await controller.GetContexts(CancellationToken.None);

        var payload = Assert.IsType<UserContextsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expectedContexts, payload.Contexts.Select(c => c.ContextKey));
    }

    [Fact]
    public async Task GetContexts_StaffAndSupplierContexts_AreNeverFilteredByTheFlag()
    {
        var userService = new Mock<IUserService>();
        var contextService = new Mock<IContextAuthorizationService>();
        userService.Setup(x => x.GetCurrentUserAsync("auth0|staff", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User { Id = "auth0|staff", Email = "s@test.com", FirstName = "S", LastName = "Taff", IsActive = true });
        contextService.Setup(x => x.GetUserContextsAsync("auth0|staff", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ContextAccess("admin", "Amministrazione", "platform_admin", ["admin.stats.read"], "/app/admin"),
                new ContextAccess("supplier", "Fornitore", "supplier", ["supplier.inbox.read"], "/supplier/inbox"),
            ]);

        var controller = BuildController(userService.Object, contextService.Object, "auth0|staff", orgTeam: false);

        var result = await controller.GetContexts(CancellationToken.None);

        var payload = Assert.IsType<UserContextsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["admin", "supplier"], payload.Contexts.Select(c => c.ContextKey));
    }

    private static MeController BuildController(
        IUserService userService,
        IContextAuthorizationService contextAuthorizationService,
        string userId,
        bool orgTeam = false)
    {
        var featureFlags = new Mock<IFeatureFlags>();
        featureFlags.Setup(f => f.IsEnabled(FeatureFlags.OrgTeam)).Returns(orgTeam);
        var controller = new MeController(userService, contextAuthorizationService, featureFlags.Object);
        var identity = new ClaimsIdentity(new[] { new Claim("sub", userId) }, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }
}
