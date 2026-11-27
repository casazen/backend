using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// The tourist tax paid online follows its booking (E25/F01/US01/T01, NF-119): the consumer of the booking events the collection has to hear, run by the
/// outbox dispatcher in its own scope under the org of the event (ADR-022-R6).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="DomainEventType.BookingCancelled"/> and <see cref="DomainEventType.BookingNoShowMarked"/>: the stay ended
/// (<see cref="ITouristTaxCollectionService.ApplyStayEndedAsync"/>): no night spent, the tax is not due and what was paid online is refunded in full; a stay
/// cancelled after the arrival raises the host's «To do».</item>
/// <item><see cref="DomainEventType.CheckInCompleted"/>, <see cref="DomainEventType.CheckOutCompleted"/> and <see cref="DomainEventType.BookingModified"/>: the
/// stay may owe a different tax now (<see cref="ITouristTaxCollectionService.ReconcileAsync"/>): a difference owed back raises the host's «To do» once.</item>
/// <item><b>Idempotent per event</b>: the service decides from the record and the booking as they are now, never from the event. <b>Out of order</b>: a
/// payment on its way when the stay ends is waited for (<see cref="TouristTaxCollectionPendingException"/>, delivered again); any other failure is logged
/// with its type and thrown again, so the outbox delivers the event again.</item>
/// <item>Nothing while <c>Features:TouristTaxOnlinePayment</c> is off. No log carries a value of the guest.</item>
/// </list>
/// </remarks>
public sealed class TouristTaxCollectionEventHandler(
    ITouristTaxCollectionService collection,
    IFeatureFlags featureFlags,
    ILogger<TouristTaxCollectionEventHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (KindOf(domainEvent.Type) is not { } kind || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.TouristTaxOnlinePayment))
            return;

        try
        {
            if (kind == StayChange.Ended)
                await collection.ApplyStayEndedAsync(domainEvent.EntityId, domainEvent.OccurredAt, ct);
            else
                await collection.ReconcileAsync(domainEvent.EntityId, ct);
        }
        catch (TouristTaxCollectionPendingException)
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}): a tourist tax payment of booking {BookingId} of org {OrgId} is on its way: the event is delivered again",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.EntityId,
                domainEvent.OrgId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Event {EventId} ({EventType}): the tourist tax collection of booking {BookingId} of org {OrgId} could not follow it ({Error}): the event is delivered again",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.EntityId,
                domainEvent.OrgId,
                ex.GetType().Name);
            throw;
        }
    }

    /// <summary>What a booking event asks of the collection; <c>null</c> for an event it does not hear.</summary>
    internal static StayChange? KindOf(DomainEventType type) => type switch
    {
        DomainEventType.BookingCancelled or DomainEventType.BookingNoShowMarked => StayChange.Ended,
        DomainEventType.CheckInCompleted or DomainEventType.CheckOutCompleted or DomainEventType.BookingModified => StayChange.Reconcile,
        _ => null,
    };

    /// <summary>The two things a booking event asks of the collection.</summary>
    internal enum StayChange
    {
        Ended = 1,
        Reconcile = 2,
    }
}
