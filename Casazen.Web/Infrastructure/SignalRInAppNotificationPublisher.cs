using Casazen.Core.Services;
using Casazen.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// <see cref="IInAppNotificationPublisher"/> on SignalR: counts the user's unread notifications and sends
/// <see cref="NotificationsHub.ChangedMethod"/> to the user's group. Singleton: the count is read in a scope of its own, so it works
/// the same from a request and from the Hangfire job (same process as the web app). <b>Never throws</b>: a failure is logged with the
/// type and ids of the event, never with a user id or a text, and the notification stays stored.
/// </summary>
public sealed class SignalRInAppNotificationPublisher(
    IHubContext<NotificationsHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<SignalRInAppNotificationPublisher> logger) : IInAppNotificationPublisher
{
    public async Task PublishAsync(string userId, string? type, Guid? entityId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            int unread;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                unread = await scope.ServiceProvider.GetRequiredService<IInAppNotificationService>()
                    .CountUnreadAsync(userId, cancellationToken);
            }

            await hub.Clients.Group(NotificationsHub.GroupFor(userId))
                .SendAsync(NotificationsHub.ChangedMethod, new NotificationsChangedMessage(unread, type, entityId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Real-time update of the notifications ({Type}, entity {EntityId}) could not be published", type, entityId);
        }
    }
}
