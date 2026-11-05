using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// The routine engine on the event bus (NF-21, ADR-022): the one path from a domain event to the routines of its org.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which routines</b>: the event type is mapped by <see cref="RoutineEventMap"/>; an event that maps to nothing starts
/// nothing. A time trigger starts only the routines whose configured offset (<see cref="TimeTrigger.OffsetHours"/> of
/// <c>EventConfig</c>, <c>days * 24 + hours</c>) equals the offset of its payload, so "2 days before" never fires for "1 day before"
/// (ADR-022-R8).</item>
/// <item><b>Once</b>: the execution key is the outbox row id (<see cref="DomainEvent.Id"/>), the same on every redelivery, and the
/// unique index on (routine, event) makes a second delivery, even a parallel one, run nothing (ADR-022-R7).</item>
/// <item><b>Failures</b>: a routine that fails is recorded on its execution (<c>Outcome = Failed</c>) by the engine and never
/// fails the event (ADR-022-R5); only a database failure escapes, so the dispatcher delivers the event again.</item>
/// <item>The payload handed to the routines is the event's <c>PayloadJson</c> (ids and codes, no personal data, ADR-022-R21) plus
/// <c>EventType</c>. The org is the event's: the scope of the handler is under that org (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class RoutineEventHandler(IRoutineService routineService, ILogger<RoutineEventHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type == DomainEventType.TimeTrigger)
        {
            await HandleTimeTriggerAsync(domainEvent, ct);
            return;
        }

        if (RoutineEventMap.Of(domainEvent.Type) is not { } routineEventType)
            return;

        if (PayloadOf(domainEvent) is not { } payload)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not a JSON object: no routine runs on it",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        await routineService.TriggerAsync(domainEvent.OrgId, routineEventType, ExecutionKeyOf(domainEvent), payload, ct);
    }

    private async Task HandleTimeTriggerAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        if (!TimeTrigger.TryReadPayload(domainEvent.PayloadJson, out var trigger)
            || RoutineEventMap.OfTimeTriggerKind(trigger.Kind) is not { } routineEventType
            || PayloadOf(domainEvent) is not { } payload)
        {
            logger.LogWarning(
                "Time trigger {EventId} of org {OrgId} has no known kind and offset: no routine runs on it",
                domainEvent.Id,
                domainEvent.OrgId);
            return;
        }

        var offset = trigger.Offset;
        await routineService.TriggerAsync(
            domainEvent.OrgId,
            routineEventType,
            ExecutionKeyOf(domainEvent),
            payload,
            routine => TimeTrigger.OffsetHours(routine.EventConfig) == offset,
            ct);
    }

    /// <summary>The idempotence key of the executions: the outbox row id, the same on every delivery of the event.</summary>
    private static string ExecutionKeyOf(DomainEvent domainEvent) => domainEvent.Id.ToString("D");

    /// <summary>The members of <c>PayloadJson</c> plus the event type; <c>null</c> when the payload is not a JSON object.</summary>
    private static Dictionary<string, object?>? PayloadOf(DomainEvent domainEvent)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(domainEvent.PayloadJson) ? "{}" : domainEvent.PayloadJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
            payload[member.Name] = member.Value;
        payload["EventType"] = domainEvent.Type.ToString();
        return payload;
    }
}
