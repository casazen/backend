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
