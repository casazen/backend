using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// A domain event as the handlers of the event bus receive it (ADR-022): what they need of its <c>OutboxEvent</c> row.
/// <see cref="Id"/> is the row's id, the same on every redelivery, so a handler that keys its work on it stays idempotent under
/// the at-least-once delivery of the dispatcher (ADR-022-R4, R7). <see cref="OrgId"/> is the org the handler works for, and the
/// only one (ADR-022-R6, R17).
/// </summary>
/// <remarks>
/// Not sealed: a typed event derives from it with the same constructor values and read-only properties parsed from
/// <see cref="PayloadJson"/>, and gets one line in <see cref="DomainEventTypeMap"/>. No personal data in clear (ADR-022-R21).
/// </remarks>
public record DomainEvent(
    Guid Id,
    Guid OrgId,
    DomainEventType Type,
    Guid EntityId,
    DateTime OccurredAt,
    string PayloadJson);
