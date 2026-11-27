using System.Collections.Concurrent;
using System.Reflection;
using Casazen.Core.Events;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// The phase 1 event bus (ADR-022-R3): in process, no broker, no new package. It runs the handlers registered in the container
/// for the event: those of its own CLR type (a typed event of <see cref="DomainEventTypeMap"/>), of every base type, and of
/// <see cref="DomainEvent"/>, which receive every event. The built-in container does not apply the contravariance of
/// <see cref="IEventHandler{T}"/>, so each level is resolved on its own.
/// </summary>
/// <remarks>
/// <para><b>One scope per handler, under the event's org</b> (ADR-022-R6, R17): each handler is resolved and run in a new DI scope,
/// with its own <c>DbContext</c>, and the scope is put under the tenant of <see cref="DomainEvent.OrgId"/> through
/// <see cref="IBackgroundTenantScope"/> before the handler is resolved: the tenant query filter of that scope sees the event's org
/// only, and the handler gets the org in the event as well.</para>
/// <para><b>Failures</b>: a failing handler does not stop the others; when one failed, <see cref="PublishAsync"/> throws after all
/// of them ran, so the dispatcher records the failure and delivers the event again (at least once, ADR-022-R4, R5). A cancelled
/// run stops at once.</para>
/// </remarks>
public sealed class InProcessEventBus(IServiceScopeFactory scopeFactory, ILogger<InProcessEventBus> logger) : IEventBus
{
    private static readonly MethodInfo InvokeMethod =
        typeof(InProcessEventBus).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, Func<object, DomainEvent, CancellationToken, Task>> Invokers = new();

    public async Task PublishAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var failed = new List<(string Handler, Exception Error)>();
        var ran = 0;
        foreach (var eventType in HandledTypesOf(domainEvent.GetType()))
        {
            var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);

            // The handlers are counted in the first scope; every handler then gets a scope of its own, in registration order.
            var count = 1;
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();

                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(domainEvent.OrgId);
                var handlers = scope.ServiceProvider.GetServices(handlerType).OfType<object>().ToList();
                count = handlers.Count;
                if (index >= count)
                    break;

                var handler = handlers[index];
                ran++;
                try
                {
                    await InvokerOf(eventType)(handler, domainEvent, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Event handler {Handler} failed on event {EventId} ({EventType}) of org {OrgId}",
                        handler.GetType().Name,
                        domainEvent.Id,
                        domainEvent.Type,
                        domainEvent.OrgId);
                    failed.Add((handler.GetType().Name, ex));
                }
            }
        }

        if (failed.Count > 0)
        {
            throw new AggregateException(
                $"{failed.Count} of {ran} handler(s) failed on event {domainEvent.Id} ({domainEvent.Type}): " +
                string.Join(", ", failed.Select(f => f.Handler)),
                failed.Select(f => f.Error));
        }
    }

    /// <summary>The event's CLR type and its base types down to <see cref="DomainEvent"/>, the most specific first.</summary>
    private static IEnumerable<Type> HandledTypesOf(Type eventType)
    {
        for (var type = eventType; type is not null && typeof(DomainEvent).IsAssignableFrom(type); type = type.BaseType)
            yield return type;
    }

    private static Func<object, DomainEvent, CancellationToken, Task> InvokerOf(Type eventType) =>
        Invokers.GetOrAdd(
            eventType,
            type => InvokeMethod.MakeGenericMethod(type).CreateDelegate<Func<object, DomainEvent, CancellationToken, Task>>());

    private static Task InvokeAsync<TEvent>(object handler, DomainEvent domainEvent, CancellationToken ct)
        where TEvent : DomainEvent
    {
        return ((IEventHandler<TEvent>)handler).HandleAsync((TEvent)domainEvent, ct);
    }
}
