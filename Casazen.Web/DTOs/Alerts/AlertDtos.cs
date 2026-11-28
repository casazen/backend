using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Alerts;

/// <summary>
/// An alert of the popup queue as <c>GET /api/me/alerts/pending</c> answers it (ADR-025-R5): references only, the same five fields the
/// SignalR message <c>alertReceived</c> carries. No name, no text, no deadline and no user: the client writes the title from
/// <see cref="AlertType"/> in the language of the user and reads the detail from the API of the entity (<see cref="EntityType"/> +
/// <see cref="EntityId"/>). A test fails if a field is added.
/// </summary>
/// <param name="AlertId">The alert, to dismiss it or snooze it.</param>
/// <param name="AlertType">The name of an <see cref="Casazen.Core.Entities.Enums.AlertType"/> member.</param>
/// <param name="EntityType">The kind of entity the alert is about (<c>Booking</c>, <c>Property</c>, ...).</param>
/// <param name="EntityId">The entity the alert is about.</param>
/// <param name="Urgency">The name of an <see cref="Casazen.Core.Entities.Enums.AlertUrgency"/> member: <c>High</c>, <c>Normal</c> or <c>Low</c>.</param>
public sealed record PendingAlertDto(Guid AlertId, string AlertType, string EntityType, Guid EntityId, string Urgency)
{
    public static PendingAlertDto From(PopupAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new PendingAlertDto(alert.Id, alert.AlertType.ToString(), alert.EntityType, alert.EntityId, alert.Urgency.ToString());
    }
}

public sealed record RemindRequest
{
    [Required(ErrorMessage = "PopupAlertRemindAtRequired")]
    public DateTimeOffset? RemindAt { get; init; }
}

public sealed record DismissRequest;
