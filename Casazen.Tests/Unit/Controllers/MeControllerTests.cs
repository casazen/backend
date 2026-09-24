using System.Security.Claims;
using Casazen.Core.Entities;
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

    private static MeController BuildController(
        IUserService userService,
        IContextAuthorizationService contextAuthorizationService,
        string userId)
    {
        var controller = new MeController(userService, contextAuthorizationService);
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
