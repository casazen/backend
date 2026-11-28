namespace Casazen.Core.Services;

/// <summary>
/// Tells the open sessions of a user, in real time, that their in-app notifications changed (a new one was written, or some were
/// read), so the bell and the badges refresh without polling. The message carries the new unread count and, when a notification was
/// written or cleared for an entity, its <paramref name="type"/> (a value of <see cref="PushTypes"/>) and id (booking or service
/// request); never a text and never a person.
/// </summary>
/// <remarks>
/// Best effort: an implementation never throws (it logs and swallows), because the notification is already stored and the client
/// reads it on the next poll or reconnect. Runbook: <c>docs/runbooks/in-app-notifications.md</c>.
/// </remarks>
public interface IInAppNotificationPublisher
{
    Task PublishAsync(string userId, string? type, Guid? entityId, CancellationToken cancellationToken = default);
}

public sealed class NoOpInAppNotificationPublisher : IInAppNotificationPublisher
{
    public Task PublishAsync(string userId, string? type, Guid? entityId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
