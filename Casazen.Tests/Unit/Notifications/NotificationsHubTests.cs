using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Hubs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Notifications;

/// <summary>The real-time channel of the notifications: who may connect, who is told what, and that a failure never escapes.</summary>
public class NotificationsHubTests
{
    private const string UserId = "auth0|anna";

    // ─── The hub ───

    [Fact]
    public void Hub_RequiresASignedInUser_AtTheDocumentedPath()
    {
        Assert.Equal("/hubs/notifications", NotificationsHub.Path);
        Assert.Equal("notificationsChanged", NotificationsHub.ChangedMethod);
        Assert.Equal(CasazenPolicies.Authenticated, Assert.Single(typeof(NotificationsHub).GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(typeof(NotificationsHub).GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Hub_ExposesNoMethodAClientCanCall()
    {
        var declared = typeof(NotificationsHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.All(declared, m => Assert.Equal("OnConnectedAsync", m.Name));
    }

    [Fact]
    public async Task OnConnectedAsync_AddsTheConnectionToTheGroupOfTheTokensSub()
    {
        var groups = new Mock<IGroupManager>();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "test")));
        var hub = new NotificationsHub { Context = context.Object, Groups = groups.Object, Clients = new Mock<IHubCallerClients>().Object };

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync("conn-1", $"user:{UserId}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_WithoutASub_JoinsNoGroup()
    {
        var groups = new Mock<IGroupManager>();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity()));
        var hub = new NotificationsHub { Context = context.Object, Groups = groups.Object, Clients = new Mock<IHubCallerClients>().Object };

        await hub.OnConnectedAsync();

        groups.VerifyNoOtherCalls();
    }

    // ─── The token in the query ───

    [Theory]
    [InlineData("/hubs/notifications", "?access_token=abc", true, "abc")]
    [InlineData("/HUBS/notifications", "?access_token=abc", true, "abc")]
    [InlineData("/api/me/notifications", "?access_token=abc", false, "")]
    [InlineData("/hubsandmore", "?access_token=abc", false, "")]
    [InlineData("/hubs/notifications", "", false, "")]
    [InlineData("/hubs/notifications", "?access_token=", false, "")]
    public void HubAccessToken_TryRead_OnlyForTheHubsPaths(string path, string query, bool expected, string token)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = path;
        request.QueryString = new QueryString(query);

        var read = HubAccessToken.TryRead(request, out var value);

        Assert.Equal(expected, read);
        Assert.Equal(token, value);
    }

    // ─── The publisher ───

    [Fact]
    public async Task PublishAsync_SendsTheCountTypeAndEntityToTheGroupOfTheUser()
    {
        var entity = Guid.NewGuid();
        var proxy = new Mock<IClientProxy>();
        var (publisher, groupName) = Publisher(proxy, unread: 4);

        await publisher.PublishAsync(UserId, PushTypes.GuestMessage, entity);

        Assert.Equal($"user:{UserId}", groupName());
        proxy.Verify(p => p.SendCoreAsync(
            "notificationsChanged",
            It.Is<object?[]>(args => args.Length == 1 && args[0]!.Equals(new NotificationsChangedMessage(4, "guest-message", entity))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_ACountOnlyChange_CarriesNullTypeAndEntity()
    {
        var proxy = new Mock<IClientProxy>();
        var (publisher, _) = Publisher(proxy, unread: 0);

        await publisher.PublishAsync(UserId, null, null);

        proxy.Verify(p => p.SendCoreAsync(
            "notificationsChanged",
            It.Is<object?[]>(args => args.Length == 1 && args[0]!.Equals(new NotificationsChangedMessage(0, null, null))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_TheHubFails_DoesNotThrow()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("socket"));
        var (publisher, _) = Publisher(proxy, unread: 1);

        await publisher.PublishAsync(UserId, null, null);
    }

    [Fact]
    public async Task PublishAsync_TheCountFails_DoesNotThrowAndSendsNothing()
    {
        var proxy = new Mock<IClientProxy>();
        var (publisher, _) = Publisher(proxy, unread: 1, countThrows: true);

        await publisher.PublishAsync(UserId, null, null);

        proxy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublishAsync_BlankUser_SendsNothing()
    {
        var proxy = new Mock<IClientProxy>();
        var (publisher, _) = Publisher(proxy, unread: 1);

        await publisher.PublishAsync(" ", null, null);

        proxy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoOpPublisher_DoesNothing()
    {
        await new NoOpInAppNotificationPublisher().PublishAsync(UserId, PushTypes.NewBooking, Guid.NewGuid());
    }

    private static (SignalRInAppNotificationPublisher Publisher, Func<string?> GroupName) Publisher(
        Mock<IClientProxy> proxy,
        int unread,
        bool countThrows = false)
    {
        string? group = null;
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Callback<string>(name => group = name).Returns(proxy.Object);
        var hub = new Mock<IHubContext<NotificationsHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var notifications = new Mock<IInAppNotificationService>();
        var count = notifications.Setup(n => n.CountUnreadAsync(UserId, It.IsAny<CancellationToken>()));
        if (countThrows)
            count.ThrowsAsync(new InvalidOperationException("db"));
        else
            count.ReturnsAsync(unread);
        var services = new ServiceCollection().AddScoped(_ => notifications.Object).BuildServiceProvider();

        return (
            new SignalRInAppNotificationPublisher(hub.Object, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SignalRInAppNotificationPublisher>.Instance),
            () => group);
    }
}
