using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Casazen.Web.Resources;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary><c>GET api/messages/unread</c>: the counters of the badges, in the scope of the caller, without any text.</summary>
public class HostMessagesControllerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IGuestMessageService> _messages = new(MockBehavior.Strict);
    private readonly Mock<IOrgContextResolver> _orgs = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();

    private HostMessagesController Controller()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|anna")], "test")),
            RequestServices = new ServiceCollection()
                .AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer())
                .BuildServiceProvider(),
        };
        return new HostMessagesController(_messages.Object, _orgs.Object, _scopes.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Fact]
    public void Unread_IsAGetUnderApiMessages_WithTheSamePolicyAsTheList()
    {
        var type = typeof(HostMessagesController);
        var action = type.GetMethod(nameof(HostMessagesController.Unread))!;

        Assert.Equal("api/messages", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("unread", action.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
    }

    [Fact]
    public async Task Unread_ReturnsTheCountersOfTheScope_AsItemsOfBookingIdAndUnreadCount()
    {
        var scope = new HostScope(OrgId, GrantedToUserId: "auth0|anna");
        var booking = Guid.NewGuid();
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _scopes.Setup(s => s.ResolveAsync("auth0|anna", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        _messages.Setup(m => m.ListUnreadForHostAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HostUnreadThread(booking, 3)]);

        var result = await Controller().Unread(CancellationToken.None);

        var dto = Assert.IsType<OkObjectResult>(result.Result).Value as HostUnreadThreadsDto;
        var item = Assert.Single(dto!.Items);
        Assert.Equal(new HostUnreadThreadDto(booking, 3), item);
        Assert.Equal(
            """{"items":[{"bookingId":"%s","unreadCount":3}]}""".Replace("%s", booking.ToString()),
            System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task Unread_NoScope_Is403_AndReadsNothing()
    {
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync((HostScope?)null);

        var result = await Controller().Unread(CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _messages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unread_NoOrg_Is403()
    {
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var result = await Controller().Unread(CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
