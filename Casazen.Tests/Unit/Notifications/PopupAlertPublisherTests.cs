using Casazen.Core.Entities.Enums;
using Casazen.Web.Hubs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Notifications;

/// <summary>
/// ADR-025-R5, R6 (E01/F03/US01/T02): the popup alert travels on the hub of the bell, to the group of its own user, as references only.
/// </summary>
public class PopupAlertPublisherTests
{
    private const string UserId = "auth0|anna";

    /// <summary>
    /// Proof (privacy; ADR-025-R5, R6): the alert goes to the group of the user it is for, on the second client method of the hub of the
    /// bell, and the message is the five references (alert id, type, entity type, entity id, urgency) with the two enums by name: no name,
    /// no text, no user. A failure of the hub never escapes.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SendsOnlyReferencesToTheGroupOfTheUser_AndNeverThrows()
    {
        var alertId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var proxy = new Mock<IClientProxy>();
        var (publisher, groupName) = Publisher(proxy);

        await publisher.PublishAsync(UserId, alertId, AlertType.AlloggiatiDeadline, "Booking", entityId, AlertUrgency.High, CancellationToken.None);

        Assert.Equal("alertReceived", NotificationsHub.AlertMethod);
        Assert.Equal($"user:{UserId}", groupName());
        proxy.Verify(
            p => p.SendCoreAsync(
                "alertReceived",
                It.Is<object?[]>(args => args.Length == 1 && args[0]!.Equals(new PopupAlertMessage(alertId, "AlloggiatiDeadline", "Booking", entityId, "High"))),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(
            ["AlertId", "AlertType", "EntityId", "EntityType", "Urgency"],
            typeof(PopupAlertMessage).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));

        var failing = new Mock<IClientProxy>();
        failing.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("socket"));
        var (failingPublisher, _) = Publisher(failing);

        await failingPublisher.PublishAsync(UserId, alertId, AlertType.CheckInPending, "Booking", entityId, AlertUrgency.Normal, CancellationToken.None);

        var blank = new Mock<IClientProxy>();
        var (blankPublisher, _) = Publisher(blank);
        await blankPublisher.PublishAsync(" ", alertId, AlertType.CheckInPending, "Booking", entityId, AlertUrgency.Normal, CancellationToken.None);
        blank.VerifyNoOtherCalls();
    }

    private static (SignalRPopupAlertPublisher Publisher, Func<string?> GroupName) Publisher(Mock<IClientProxy> proxy)
    {
        string? group = null;
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Callback<string>(name => group = name).Returns(proxy.Object);
        var hub = new Mock<IHubContext<NotificationsHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        return (new SignalRPopupAlertPublisher(hub.Object, NullLogger<SignalRPopupAlertPublisher>.Instance), () => group);
    }
}
