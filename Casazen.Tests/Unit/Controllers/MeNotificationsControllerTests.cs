using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// UI-12a: <c>api/me/notifications</c> acts for the caller only (the token's <c>sub</c>), behind the flag, with the routes the
/// frontend will call; the rules about whose rows are in <see cref="IInAppNotificationService"/> and are tested there.
/// </summary>
public class MeNotificationsControllerTests
{
    private const string UserId = "auth0|anna";

    private readonly Mock<IInAppNotificationService> _service = new(MockBehavior.Strict);

    private MeNotificationsController Controller(string? sub = UserId)
    {
        var http = new DefaultHttpContext();
        if (sub is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "test"));

        return new MeNotificationsController(_service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // ─── The contract ───

    [Fact]
    public void Controller_IsBehindTheFlag_ForSignedInUsersOnly_UnderApiMeNotifications()
    {
        var type = typeof(MeNotificationsController);

        Assert.Equal("api/me/notifications", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(FeatureFlags.InAppNotifications, Assert.Single(type.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.Equal(CasazenPolicies.Authenticated, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Theory]
    [InlineData(nameof(MeNotificationsController.List), "GET", null)]
    [InlineData(nameof(MeNotificationsController.UnreadCount), "GET", "unread-count")]
    [InlineData(nameof(MeNotificationsController.MarkRead), "POST", "{id:guid}/read")]
    [InlineData(nameof(MeNotificationsController.MarkAllRead), "POST", "read-all")]
    public void Actions_HaveTheRoutesOfTheTask(string action, string verb, string? template)
    {
        var method = typeof(MeNotificationsController).GetMethod(action)!;

        var http = Assert.Single(method.GetCustomAttributes<HttpMethodAttribute>());
        Assert.Equal([verb], http.HttpMethods);
        Assert.Equal(template, http.Template);
    }

    [Fact]
    public void Actions_AreTheFourOfTheTaskAndNoOther()
    {
        var actions = typeof(MeNotificationsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["List", "MarkAllRead", "MarkRead", "UnreadCount"], actions);
    }

    [Fact]
    public void Actions_NeverTakeTheUserFromTheRequest()
    {
        // The user is the caller's: no action has a parameter that could name another one.
        foreach (var method in typeof(MeNotificationsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                p => p.Name!.Contains("user", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("org", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ─── List ───

    [Fact]
    public async Task List_ReturnsThePageOfTheCaller_PrivateAndNeverCached()
    {
        var id = Guid.NewGuid();
        var entity = Guid.NewGuid();
        var at = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        _service
            .Setup(s => s.ListAsync(UserId, true, 2, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InAppNotificationPage(
                [new InAppNotificationItem(id, PushTypes.NewBooking, entity, at, null)], TotalCount: 11, Page: 2, PageSize: 5));
        var controller = Controller();

        var result = await controller.List(unread: true, page: 2, pageSize: 5);

        var body = Assert.IsType<PagedResultDto<InAppNotificationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var item = Assert.Single(body.Items);
        Assert.Equal(new InAppNotificationDto(id, PushTypes.NewBooking, entity, at, null), item);
        Assert.Equal((11, 2, 5), (body.TotalCount, body.Page, body.PageSize));
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task List_TheDefaultsAreTheFirstPageOfTwenty_AllOfThem()
    {
        _service
            .Setup(s => s.ListAsync(UserId, false, 1, InAppNotificationLimits.DefaultPageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InAppNotificationPage([], 0, 1, InAppNotificationLimits.DefaultPageSize));

        var result = await Controller().List();

        Assert.IsType<OkObjectResult>(result.Result);
        _service.Verify(s => s.ListAsync(UserId, false, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_WithoutAUserIdInTheToken_IsUnauthorized_AndReadsNothing()
    {
        var result = await Controller(sub: null).List();

        Assert.IsType<UnauthorizedResult>(result.Result);
        _service.VerifyNoOtherCalls();
    }

    // ─── Count ───

    [Fact]
    public async Task UnreadCount_ReturnsTheNumberOfTheCaller()
    {
        _service.Setup(s => s.CountUnreadAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(7);
        var controller = Controller();

        var result = await controller.UnreadCount(CancellationToken.None);

        Assert.Equal(new UnreadNotificationCountDto(7), Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task UnreadCount_WithoutAUserIdInTheToken_IsUnauthorized()
    {
        var result = await Controller(sub: null).UnreadCount(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        _service.VerifyNoOtherCalls();
    }

    // ─── Mark as read ───

    [Fact]
    public async Task MarkRead_MarksTheNotificationOfTheCaller_204()
    {
        var id = Guid.NewGuid();
        _service.Setup(s => s.MarkReadAsync(UserId, id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await Controller().MarkRead(id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _service.Verify(s => s.MarkReadAsync(UserId, id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkRead_ANotificationOfSomebodyElse_IsTheNotFoundOfTheService_NeverAForbidden()
    {
        // The service raises NotFoundException (404 through the error middleware) for everything the caller does not own.
        var id = Guid.NewGuid();
        _service
            .Setup(s => s.MarkReadAsync(UserId, id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("not yours"));

        await Assert.ThrowsAsync<NotFoundException>(() => Controller().MarkRead(id, CancellationToken.None));
    }

    [Fact]
    public async Task MarkRead_WithoutAUserIdInTheToken_IsUnauthorized()
    {
        var result = await Controller(sub: null).MarkRead(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkAllRead_MarksTheNotificationsOfTheCaller_204()
    {
        _service.Setup(s => s.MarkAllReadAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var result = await Controller().MarkAllRead(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _service.Verify(s => s.MarkAllReadAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkAllRead_WithoutAUserIdInTheToken_IsUnauthorized()
    {
        var result = await Controller(sub: null).MarkAllRead(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        _service.VerifyNoOtherCalls();
    }
}
