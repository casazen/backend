using Casazen.Core.Events;

namespace Casazen.Core.Services;

/// <summary>
/// The event bus (ADR-022-R3): delivers a domain event to the handlers registered for it. Only the outbox dispatcher publishes;
/// a domain service never calls it and records its events with <see cref="IDomainEventRecorder"/> instead (ADR-022-R1).
/// Phase 1 runs in process; a broker adapter is another implementation, with no change to the producers or to the outbox.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Runs every handler of the event, each in its own scope under the event's org; returns when all of them returned and throws
    /// when one of them failed, after the others ran (the event is then delivered again, so handlers stay idempotent).
    /// </summary>
    Task PublishAsync(DomainEvent domainEvent, CancellationToken ct);
}
