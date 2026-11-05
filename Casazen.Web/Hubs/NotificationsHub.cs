using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Casazen.Web.Hubs;

/// <summary>
/// Real-time channel of the in-app notifications (UI-12a): <c>/hubs/notifications</c>. A client only listens: the server calls the
/// client method <see cref="ChangedMethod"/> with a <see cref="NotificationsChangedMessage"/> whenever the unread count of the
/// connected user changes (a notification written, some read). Nothing can be sent to the hub by a client, and a message goes to
/// the user's own group only.
/// </summary>
/// <remarks>
/// <para><b>Authentication.</b> The same Auth0 JWT as the API (<see cref="CasazenPolicies.Authenticated"/>): browsers cannot set a header on a
/// WebSocket or server-sent events request, so for paths under <c>/hubs</c> the token is also read from <c>?access_token=</c>
/// (<c>JwtBearerEvents.OnMessageReceived</c>). The user is the token's <c>sub</c>, the same id as <c>api/me/notifications</c>.</para>
/// <para><b>Scale.</b> Connections live in the memory of this process; a message is delivered only to the sessions connected to the
/// instance that publishes it. The app runs as one instance (Railway); a second one needs a backplane (see the runbook).</para>
/// </remarks>
[Authorize(Policy = CasazenPolicies.Authenticated)]
public sealed class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";

    /// <summary>Name of the client method the server calls.</summary>
    public const string ChangedMethod = "notificationsChanged";

    /// <summary>
    /// Name of the second client method the server calls: a popup alert for the connected user (ADR-025), with a
    /// <see cref="PopupAlertMessage"/>. The alert travels on the connection of the bell, to the same user group, as one more method.
    /// </summary>
    public const string AlertMethod = "alertReceived";

    /// <summary>The group of the connections of one user.</summary>
    public static string GroupFor(string userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.GetUserId() is { Length: > 0 } userId)
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId));

        await base.OnConnectedAsync();
    }
}

/// <summary>The only argument of <see cref="NotificationsHub.ChangedMethod"/> (camelCase JSON: <c>unreadCount</c>, <c>type</c>, <c>entityId</c>).</summary>
/// <param name="UnreadCount">Unread notifications of the user, now.</param>
/// <param name="Type">Type of the notification written or cleared (a value of <c>PushTypes</c>); <c>null</c> when only the count changed.</param>
/// <param name="EntityId">The booking or service request it is about; <c>null</c> when there is none.</param>
public sealed record NotificationsChangedMessage(int UnreadCount, string? Type, Guid? EntityId);

/// <summary>
/// The only argument of <see cref="NotificationsHub.AlertMethod"/> (camelCase JSON: <c>alertId</c>, <c>alertType</c>, <c>entityType</c>,
/// <c>entityId</c>, <c>urgency</c>): references only, no name and no text (ADR-025-R5); the client reads the detail from the API of the
/// entity. The two enums go by name, the names <c>GET /api/me/alerts/pending</c> writes (the SignalR JSON protocol would write numbers).
/// </summary>
/// <param name="AlertId">The stored alert, to dismiss it or snooze it.</param>
/// <param name="AlertType">The name of an <see cref="Casazen.Core.Entities.Enums.AlertType"/> member.</param>
/// <param name="EntityType">The kind of entity the alert is about (<c>Booking</c>, <c>Property</c>, ...).</param>
/// <param name="EntityId">The entity the alert is about.</param>
/// <param name="Urgency">The name of an <see cref="Casazen.Core.Entities.Enums.AlertUrgency"/> member.</param>
public sealed record PopupAlertMessage(Guid AlertId, string AlertType, string EntityType, Guid EntityId, string Urgency);
