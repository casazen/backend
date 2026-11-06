using System.Text.Json;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// A property was accepted by the channel manager for the first time (E02/F03/US01/T02, ADR-022): its structure and content are on Octorate. It is
/// recorded once for each property, in the same save as the state of the channel rows (the outbox), and E08/F05/US02/T02 consumes it to send the
/// calendar, the prices and the restrictions. <see cref="DomainEvent.EntityId"/> is the property.
/// </summary>
public sealed record ListingPublishedEvent : DomainEvent
{
    public ListingPublishedEvent(
        Guid id,
        Guid orgId,
        DomainEventType type,
        Guid entityId,
        DateTime occurredAt,
        string payloadJson)
        : base(id, orgId, type, entityId, occurredAt, payloadJson)
    {
        var payload = ListingPublishedPayload.Parse(payloadJson);
        PropertyId = payload.PropertyId == Guid.Empty ? entityId : payload.PropertyId;
        AccommodationId = payload.AccommodationId;
    }

    /// <summary>The property that was published.</summary>
    public Guid PropertyId { get; }

    /// <summary>The accommodation it is on Octorate, when the payload says it.</summary>
    public string? AccommodationId { get; }
}

/// <summary>The stored payload of <see cref="DomainEventType.ListingPublished"/>: ids only, no text of the listing (ADR-022-R21).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="AccommodationId">The accommodation on Octorate.</param>
public sealed record ListingPublishedPayload(Guid PropertyId, string? AccommodationId)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The payload of a stored event; a payload that cannot be read (an anonymized dead letter keeps <c>{}</c>) gives no ids.</summary>
    public static ListingPublishedPayload Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new ListingPublishedPayload(Guid.Empty, null);

        try
        {
            return JsonSerializer.Deserialize<ListingPublishedPayload>(payloadJson, Options) ?? new ListingPublishedPayload(Guid.Empty, null);
        }
        catch (JsonException)
        {
            return new ListingPublishedPayload(Guid.Empty, null);
        }
    }
}
