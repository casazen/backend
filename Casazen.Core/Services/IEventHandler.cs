using Casazen.Core.Events;

namespace Casazen.Core.Services;

/// <summary>
/// A subscriber of the event bus (ADR-022-R3). A handler of a typed event (mapped in <see cref="DomainEventTypeMap"/>) receives
/// the events of that type; a handler of <see cref="DomainEvent"/> receives every event and filters on
/// <see cref="DomainEvent.Type"/>.
/// </summary>
/// <remarks>
/// Delivery is at least once: the same event (same <see cref="DomainEvent.Id"/>) can arrive again after a failure or a crash, so
/// a handler is idempotent (ADR-022-R7). It works for <see cref="DomainEvent.OrgId"/> only (ADR-022-R6, R17).
/// </remarks>
public interface IEventHandler<in T> where T : DomainEvent
{
    Task HandleAsync(T domainEvent, CancellationToken ct);
}
