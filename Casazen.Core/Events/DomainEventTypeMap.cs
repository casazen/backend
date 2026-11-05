using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// The one place that says which CLR type a stored event becomes (ADR-022-R3): a <see cref="DomainEventType"/> without a line
/// here is a plain <see cref="DomainEvent"/>, one with a line is built as its typed event, so the handlers registered for that
/// typed event run. Without its line a typed handler would never run: the dispatcher builds every event through
/// <see cref="Create"/>.
/// </summary>
/// <remarks>
/// A typed event is a record deriving from <see cref="DomainEvent"/> with a public constructor taking the same values
/// (<c>Guid Id, Guid OrgId, DomainEventType Type, Guid EntityId, DateTime OccurredAt, string PayloadJson</c>) and read-only
/// properties parsed from <c>PayloadJson</c>, never data that is not in the row. The task that adds a typed event and its handler
/// adds its line to <see cref="TypedEvents"/> in the same change.
/// </remarks>
public static class DomainEventTypeMap
{
    private static readonly Type[] ConstructorSignature =
        [typeof(Guid), typeof(Guid), typeof(DomainEventType), typeof(Guid), typeof(DateTime), typeof(string)];

    /// <summary>The typed events, one line each.</summary>
    private static readonly IReadOnlyDictionary<DomainEventType, Type> TypedEvents = new Dictionary<DomainEventType, Type>
    {
        // A notification of the Octorate webhook (E02/F01/US01/T03): event name, accommodation and object id, read from the payload.
        [DomainEventType.OctorateWebhookReceived] = typeof(OctorateWebhookReceivedEvent),
    };

    private static readonly ConcurrentDictionary<Type, Func<Guid, Guid, DomainEventType, Guid, DateTime, string, DomainEvent>> Factories = new();

    /// <summary>The event types that have a typed event, and that event's CLR type.</summary>
    public static IReadOnlyDictionary<DomainEventType, Type> Typed => TypedEvents;

    /// <summary>The CLR type an event of <paramref name="type"/> is built as: its typed event, or <see cref="DomainEvent"/>.</summary>
    public static Type ClrTypeOf(DomainEventType type) =>
        TypedEvents.TryGetValue(type, out var clrType) ? clrType : typeof(DomainEvent);

    /// <summary>The event of an outbox row, typed when its <see cref="OutboxEvent.EventType"/> has a line in the map.</summary>
    /// <exception cref="InvalidOperationException">The mapped type is not a usable typed event (a programming error).</exception>
    public static DomainEvent Create(OutboxEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var clrType = ClrTypeOf(row.EventType);
        if (clrType == typeof(DomainEvent))
            return new DomainEvent(row.Id, row.OrgId, row.EventType, row.EntityId, row.OccurredAt, row.PayloadJson);

        var factory = Factories.GetOrAdd(clrType, BuildFactory);
        return factory(row.Id, row.OrgId, row.EventType, row.EntityId, row.OccurredAt, row.PayloadJson);
    }

    private static Func<Guid, Guid, DomainEventType, Guid, DateTime, string, DomainEvent> BuildFactory(Type clrType)
    {
        if (!typeof(DomainEvent).IsAssignableFrom(clrType) || clrType.IsAbstract)
            throw new InvalidOperationException($"{clrType.Name} is mapped as a domain event but is not a concrete {nameof(DomainEvent)}.");

        var constructor = clrType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, ConstructorSignature)
            ?? throw new InvalidOperationException(
                $"{clrType.Name} needs a public constructor (Guid Id, Guid OrgId, DomainEventType Type, Guid EntityId, " +
                "DateTime OccurredAt, string PayloadJson) to be built from an outbox row.");

        // A compiled constructor call: no reflection per event, and an exception of the constructor reaches the caller as it is.
        var parameters = ConstructorSignature.Select(Expression.Parameter).ToArray();
        var body = Expression.Convert(Expression.New(constructor, parameters), typeof(DomainEvent));
        return Expression.Lambda<Func<Guid, Guid, DomainEventType, Guid, DateTime, string, DomainEvent>>(body, parameters).Compile();
    }
}
