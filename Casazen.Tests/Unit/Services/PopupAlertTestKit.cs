using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>One push of a popup alert, as the publisher received it (ADR-025): references only.</summary>
internal sealed record PublishedAlert(string UserId, Guid AlertId, AlertType Type, string EntityType, Guid EntityId, AlertUrgency Urgency);

/// <summary>The delivery port of the popup alerts under the test's control: remembers every push.</summary>
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
