using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// <see cref="IPopupAlertPublisher"/> on SignalR: sends <see cref="NotificationsHub.AlertMethod"/> with a
/// <see cref="PopupAlertMessage"/> to the group of the user on the hub of the bell (<c>/hubs/notifications</c>), the one connection the
/// frontend already opens; there is no second hub. Singleton, with nothing but the hub context, so it works the same from a request and
/// from the Hangfire jobs (same process as the web app). <b>Never throws</b>: a failure is logged with the type and the ids of the alert,
/// never with a user id or a text, and the alert stays stored.
/// </summary>
public sealed class SignalRPopupAlertPublisher(
    IHubContext<NotificationsHub> hub,
    ILogger<SignalRPopupAlertPublisher> logger) : IPopupAlertPublisher
{
    public async Task PublishAsync(
        string userId,
        Guid alertId,
        AlertType alertType,
        string entityType,
        Guid entityId,
        AlertUrgency urgency,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            var message = new PopupAlertMessage(alertId, alertType.ToString(), entityType, entityId, urgency.ToString());
            await hub.Clients.Group(NotificationsHub.GroupFor(userId)).SendAsync(NotificationsHub.AlertMethod, message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Real-time popup alert {AlertId} ({AlertType} on {EntityType} {EntityId}) could not be published",
                alertId,
                alertType,
                entityType,
                entityId);
        }
    }
}
