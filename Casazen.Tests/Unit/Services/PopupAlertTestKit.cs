using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

internal sealed record PublishedAlert(string UserId, Guid AlertId, AlertType Type, string EntityType, Guid EntityId, AlertUrgency Urgency);

internal sealed class RecordingPopupAlertPublisher : IPopupAlertPublisher
{
    public List<PublishedAlert> Sent { get; } = [];

    public Task PublishAsync(
        string userId,
        Guid alertId,
        AlertType alertType,
        string entityType,
        Guid entityId,
        AlertUrgency urgency,
        CancellationToken ct)
    {
        Sent.Add(new PublishedAlert(userId, alertId, alertType, entityType, entityId, urgency));
        return Task.CompletedTask;
    }
}
