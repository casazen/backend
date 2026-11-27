using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// Keeps the scheduled messages of a booking in step with the booking (E05/F07/US01/T02, NF-71): when a booking is confirmed or its dates change, its messages are planned or move with it
/// (<see cref="IScheduledMessageService.PlanBookingAsync"/>); when it is cancelled, its open messages are cancelled with their «To do» (<see cref="IScheduledMessageService.CancelForBookingAsync"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which events</b>: <c>BookingConfirmed</c>, <c>BookingModified</c> and <c>BookingCancelled</c>, of any origin (a change that comes from the channel manager moves the messages too). Any other
/// type is ignored. The booking is read when the event is handled, never taken from its payload, and what the event says is done only if the stay is still so (a cancellation that was undone since
/// cancels nothing), so events that arrive out of order give the same result.</item>
/// <item><b>Once</b>: the plan is idempotent (a row is written once, a row that is up to date is left as it is), so an event delivered again does nothing more.</item>
/// <item>A no-op while <c>Features:ScheduledMessages</c> is off (the service reads and writes nothing); the org is the event's (ADR-022-R6). A failure of the database is not swallowed: the event is delivered again.</item>
/// </list>
/// </remarks>
public sealed class ScheduledMessageBookingHandler(
    IScheduledMessageService messages,
    IFeatureFlags featureFlags,
    ILogger<ScheduledMessageBookingHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingConfirmed or DomainEventType.BookingModified or DomainEventType.BookingCancelled) || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.ScheduledMessages))
            return;

        var changed = domainEvent.Type == DomainEventType.BookingCancelled
            ? await messages.CancelForBookingAsync(domainEvent.EntityId, ScheduledMessageReasons.BookingCancelled, ct)
            : await messages.PlanBookingAsync(domainEvent.EntityId, ct);

        if (changed > 0)
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}) of org {OrgId}: {Changed} scheduled messages of booking {BookingId} written or changed",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId,
                changed,
                domainEvent.EntityId);
        }
    }
}
