using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// Closes the "To do" items whose condition an event says is met (ADR-026-R4, ADR-022): the check-in of a stay is done, so its
/// <see cref="ActivityType.CheckInPending"/> item is closed, and so on (<see cref="Rules"/>). The item is closed by the system
/// through <see cref="IInternalActivityService.CloseAsync"/>: <c>Status</c> "Completed", <c>CompletedAt</c> and
/// <c>ClosedBy</c> = <see cref="InternalActivity.ClosedBySystem"/>; the row is kept for the audit and leaves the list.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which items</b>: the open To do rows of the event's org about the event's entity, a booking
/// (<see cref="InternalActivity.BookingEntityType"/>, <see cref="DomainEvent.EntityId"/> = the booking id, ADR-022), of a type
/// the event satisfies. An event without a rule closes nothing. A task that raises a new kind of item adds the event that
/// closes it here.</item>
/// <item><b>Once</b>: only open items are read, and closing an item already closed changes nothing, so a redelivered event
/// (at least once, ADR-022-R7) closes nothing more.</item>
/// <item>The org is the event's (ADR-022-R6): the bus runs the handler under it, and the query names it too.</item>
/// </list>
/// </remarks>
public sealed class ActivityAutoCloseHandler(
    AppDbContext context,
    IInternalActivityService activities,
    ILogger<ActivityAutoCloseHandler> logger) : IEventHandler<DomainEvent>
{
    /// <summary>The types of item each event satisfies: the check-in done, the check-out done, the Alloggiati receipt arrived.</summary>
    internal static readonly IReadOnlyDictionary<DomainEventType, ActivityType[]> Rules = new Dictionary<DomainEventType, ActivityType[]>
    {
        [DomainEventType.CheckInCompleted] = [ActivityType.CheckInPending],
        [DomainEventType.CheckOutCompleted] = [ActivityType.CheckOutPending],
        [DomainEventType.AlloggiatiReceiptArrived] = [ActivityType.AlloggiatiDeadline, ActivityType.ReceiptToRetrieve],
    };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!Rules.TryGetValue(domainEvent.Type, out var types) || domainEvent.EntityId == Guid.Empty)
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var typeNames = types.Select(type => type.ToString()).ToList();
        var open = await context.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId
                        && typeNames.Contains(a.ActivityType))
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in open)
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);

        if (open.Count > 0)
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}) of org {OrgId} closed {Count} To do item(s) of booking {BookingId}",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId,
                open.Count,
                bookingId);
        }
    }
}
