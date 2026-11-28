using Casazen.Core.Events;

namespace Casazen.Core.Services;

public interface IEventBus
{
    /// <summary>
    /// Runs every handler of the event, each in its own scope under the event's org; returns when all of them returned and throws
    /// when one of them failed, after the others ran (the event is then delivered again, so handlers stay idempotent).
    /// </summary>
    Task PublishAsync(DomainEvent domainEvent, CancellationToken ct);
}
