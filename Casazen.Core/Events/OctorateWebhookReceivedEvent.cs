using System.Text.Json;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// A notification Octorate sent to the webhook (ADR-023-R7, ADR-022-R19), as the handlers of the event bus receive it: which event,
/// for which accommodation and which object (reservation, thread or content), and when it arrived. Nothing else of the body is kept:
/// the stored payload holds ids only (ADR-022-R21), and a handler re-reads the object through <c>IOctorateApiClient</c> before any
/// effect, because the body is never trusted (Octorate signs nothing).
/// </summary>
/// <remarks>
/// <see cref="DomainEvent.EntityId"/> is the <c>OctorateListingMapping</c> of the accommodation in the event's org, the row the
/// controller resolved the org from. Octorate notifies CasaZen's own writes too: a handler ignores the echo of a change it made.
/// </remarks>
public sealed record OctorateWebhookReceivedEvent : DomainEvent
{
    public OctorateWebhookReceivedEvent(
        Guid id,
        Guid orgId,
        DomainEventType type,
        Guid entityId,
        DateTime occurredAt,
        string payloadJson)
        : base(id, orgId, type, entityId, occurredAt, payloadJson)
    {
        var payload = OctorateWebhookPayload.Parse(payloadJson);
        WebhookType = payload.WebhookType;
        AccommodationId = payload.AccommodationId;
        ObjectId = payload.ObjectId;
        ReceivedAt = payload.ReceivedAt == default ? occurredAt : payload.ReceivedAt;
    }

    public string WebhookType { get; }

    public string AccommodationId { get; }

    public string? ObjectId { get; }

    public DateTime ReceivedAt { get; }
}

/// <summary>
/// The stored payload of <see cref="DomainEventType.OctorateWebhookReceived"/>: ids and the event name only, never the body of the call
/// (ADR-022-R21). Serialized with the names it declares.
/// </summary>
/// <param name="WebhookType">The Octorate event name.</param>
/// <param name="AccommodationId">The accommodation id on Octorate.</param>
/// <param name="ObjectId">The reservation, thread or content id, when the notification names one.</param>
/// <param name="ReceivedAt">When the call arrived (UTC).</param>
public sealed record OctorateWebhookPayload(string WebhookType, string AccommodationId, string? ObjectId, DateTime ReceivedAt)
{
    public static OctorateWebhookPayload Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new OctorateWebhookPayload(string.Empty, string.Empty, null, default);

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new OctorateWebhookPayload(string.Empty, string.Empty, null, default);

            return new OctorateWebhookPayload(
                StringOf(root, nameof(WebhookType)) ?? string.Empty,
                StringOf(root, nameof(AccommodationId)) ?? string.Empty,
                StringOf(root, nameof(ObjectId)),
                DateOf(root, nameof(ReceivedAt)));
        }
        catch (JsonException)
        {
            return new OctorateWebhookPayload(string.Empty, string.Empty, null, default);
        }
    }

    private static string? StringOf(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }

    private static DateTime DateOf(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && property.Value.TryGetDateTime(out var value))
            {
                return DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc);
            }
        }

        return default;
    }
}

public static class OctorateWebhookEvents
{
    public const string ReservationCreated = "RESERVATION_CREATED";
    public const string ReservationChange = "RESERVATION_CHANGE";
    public const string ReservationCancelled = "RESERVATION_CANCELLED";
    public const string ReservationConfirmed = "RESERVATION_CONFIRMED";
    public const string ChatMessageReceived = "CHAT_MESSAGE_RECEIVED";
    public const string ContentNotification = "CONTENT_NOTIFICATION";
    public const string ContentPush = "CONTENT_PUSH";
    public const string PortalSubscriptionCalendar = "PORTAL_SUBSCRIPTION_CALENDAR";

    public static IReadOnlyList<string> All { get; } =
    [
        ReservationCreated,
        ReservationChange,
        ReservationCancelled,
        ReservationConfirmed,
        ChatMessageReceived,
        ContentNotification,
        ContentPush,
        PortalSubscriptionCalendar,
    ];

    public static string? Normalize(string? name)
    {
        var candidate = name?.Trim().ToUpperInvariant();
        return candidate is not null && All.Contains(candidate, StringComparer.Ordinal) ? candidate : null;
    }
}
