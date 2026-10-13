using System.Reflection;
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

/// <summary>
/// UI-13a: <c>PUT /api/me/last-context</c> remembers the area the caller entered last, only among the contexts
/// <c>GET /api/me/contexts</c> lists for it; and that endpoint tells the stored area only while the caller can still enter it.
/// </summary>
public class MeControllerLastContextTests
{
    private const string UserId = "auth0|owner";

    private static readonly ContextAccess ShortRent = new("short-rent", "Affitti brevi", "property_owner", ["booking.read"], "/app/short-rent");
    private static readonly ContextAccess LongRent = new("long-rent", "Affitti lungo termine", "long_term_landlord", ["lease.read"], "/app/long-rent/leases");
    private static readonly ContextAccess Account = new("account", "Amministrazione", "org_owner", ["org.billing.manage"], "/app/account");

    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IContextAuthorizationService> _contexts = new();

    private MeController NewController(bool orgTeam = false, bool active = true, string? lastUsed = null, params ContextAccess[] contexts)
    {
        var user = new User { Id = UserId, Email = "o@test.com", FirstName = "O", LastName = "Wner", IsActive = active, LastUsedContextKey = lastUsed };
        _users.Setup(u => u.GetCurrentUserAsync(UserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(user);
        _users.Setup(u => u.SetLastUsedContextAsync(UserId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _contexts.Setup(c => c.GetUserContextsAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(contexts.Length == 0 ? [ShortRent, LongRent] : contexts);

        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OrgTeam)).Returns(orgTeam);
        var controller = new MeController(_users.Object, _contexts.Object, flags.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }

    private static SetLastContextRequest Body(string? key) => new() { ContextKey = key };

    [Fact]
    public async Task Put_AContextTheCallerCanEnter_IsStored_AndAnswered()
    {
        var controller = NewController();

        var result = await controller.PutLastContext(Body("long-rent"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("long-rent", Assert.IsType<LastContextResponse>(ok.Value).LastUsedContextKey);
        _users.Verify(u => u.SetLastUsedContextAsync(UserId, "long-rent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("SHORT-RENT")]
    [InlineData("  short-rent  ")]
    public async Task Put_TheMatchIgnoresCaseAndSpaces_AndTheKeyIsStoredAsTheListSpellsIt(string typed)
    {
        var controller = NewController();

        var result = await controller.PutLastContext(Body(typed), CancellationToken.None);

        Assert.Equal("short-rent", Assert.IsType<LastContextResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).LastUsedContextKey);
        _users.Verify(u => u.SetLastUsedContextAsync(UserId, "short-rent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("supplier")] // a context the caller does not have
    [InlineData("admin")]
    [InlineData("nonexistent")]
    [InlineData("short-rent; DROP TABLE")]
    [InlineData("   ")]
    public async Task Put_AContextTheCallerCannotEnter_IsRefused422_AndNothingIsStored(string key)
    {
        var controller = NewController();

        var result = await controller.PutLastContext(Body(key), CancellationToken.None);

        var problem = AssertProblem(result.Result, StatusCodes.Status422UnprocessableEntity);
        Assert.Equal(LastContextErrors.ContextNotAccessible, problem.Extensions["code"]);
        Assert.Equal("context_not_accessible", LastContextErrors.ContextNotAccessible);
        _users.Verify(u => u.SetLastUsedContextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(true, StatusCodes.Status200OK)]
    public async Task Put_TheAccountContext_IsAccessibleOnlyWhereTheListTellsIt(bool orgTeam, int expected)
    {
        // GET /api/me/contexts hides the account context while the flag is off: the same list decides here.
        var controller = NewController(orgTeam, contexts: [ShortRent, Account]);

        var result = await controller.PutLastContext(Body("account"), CancellationToken.None);

        var status = result.Result is ObjectResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;
        Assert.Equal(expected, status);
        _users.Verify(
            u => u.SetLastUsedContextAsync(UserId, "account", It.IsAny<CancellationToken>()),
            expected == StatusCodes.Status200OK ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Put_AnInactiveAccount_Gets403AccountInactive()
    {
        var controller = NewController(active: false);

        var result = await controller.PutLastContext(Body("short-rent"), CancellationToken.None);

        var problem = AssertProblem(result.Result, StatusCodes.Status403Forbidden);
        Assert.Equal(ProblemCodes.AccountInactive, problem.Extensions["code"]);
        _users.Verify(u => u.SetLastUsedContextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Put_NoSubject_IsUnauthorized()
    {
        var controller = NewController();
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await controller.PutLastContext(Body("short-rent"), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public void Put_IsAnAuthenticatedEndpoint_NotBehindTheFeatureFlag_WithABoundedBody()
    {
        var action = typeof(MeController).GetMethod(nameof(MeController.PutLastContext))!;

        Assert.Equal("last-context", action.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpPutAttribute>()!.Template);
        Assert.Empty(action.GetCustomAttributes<FeatureGateAttribute>());
        Assert.Empty(typeof(MeController).GetCustomAttributes<FeatureGateAttribute>());

        var key = typeof(SetLastContextRequest).GetProperty(nameof(SetLastContextRequest.ContextKey))!;
        Assert.NotNull(key.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>());
        Assert.Equal(64, key.GetCustomAttribute<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()!.Length);
    }

    // ─── GET /api/me/contexts gives it back ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_TheStoredContext_IsGivenBack_AsTheListSpellsIt()
    {
        var controller = NewController(lastUsed: "LONG-RENT");

        var result = await controller.GetContexts(CancellationToken.None);

        var payload = Assert.IsType<UserContextsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("long-rent", payload.LastUsedContextKey);
    }

    [Theory]
    [InlineData("supplier")] // an access the caller no longer has
    [InlineData("account")] // listed only with the flag on
    public async Task Get_AStoredContextTheCallerCannotEnterAnymore_IsNotGivenBack(string stored)
    {
        var controller = NewController(orgTeam: false, lastUsed: stored, contexts: [ShortRent, Account]);

        var result = await controller.GetContexts(CancellationToken.None);

        var payload = Assert.IsType<UserContextsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Null(payload.LastUsedContextKey);
    }

    [Fact]
    public async Task Get_NothingStored_GivesNull()
    {
        var controller = NewController();

        var payload = Assert.IsType<UserContextsResponse>(Assert.IsType<OkObjectResult>((await controller.GetContexts(CancellationToken.None)).Result).Value);

        Assert.Null(payload.LastUsedContextKey);
    }

    private static ProblemDetails AssertProblem(IActionResult? result, int status)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }
}
