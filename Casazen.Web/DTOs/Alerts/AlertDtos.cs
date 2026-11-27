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
    /// <summary>The alert of a stored row.</summary>
    public static PendingAlertDto From(PopupAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new PendingAlertDto(alert.Id, alert.AlertType.ToString(), alert.EntityType, alert.EntityId, alert.Urgency.ToString());
    }
}

/// <summary>Body of <c>PATCH /api/me/alerts/{id}/remind</c>: when the alert comes back («Remind me later»).</summary>
public sealed record RemindRequest
{
    /// <summary>
    /// The instant the alert is pending again, ISO 8601 with its offset (<c>2026-11-06T09:00:00+01:00</c> or <c>…Z</c>). Not later than
    /// the legal deadline of the alert, when it has one.
    /// </summary>
    [Required(ErrorMessage = "PopupAlertRemindAtRequired")]
    public DateTimeOffset? RemindAt { get; init; }
}

/// <summary>Body of <c>POST /api/me/alerts/{id}/dismiss</c>: none. The type exists so the call is documented; a body, if sent, is ignored.</summary>
public sealed record DismissRequest;
