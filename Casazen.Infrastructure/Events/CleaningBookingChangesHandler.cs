using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// Keeps the automatic cleaning of a stay in step with the stay (E24/F01/US01/T02, NF-120): when the dates of the booking change the cleaning moves with them, when
/// the booking is cancelled the cleaning is cancelled with it. The rules are the ones of <see cref="ICleaningAutomationService"/>: only the cleaning the automation
/// made (the key of the stay), only while nobody took it (a request that waits for its answer, a staff activity not started) or, for a cancellation, while the work
/// has not started; a cleaning the supplier took is left to the host, with a "To do" and an alert.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which events</b>: <c>BookingModified</c> and <c>BookingCancelled</c>, of any origin (a change that comes from the channel manager moves the cleaning too). Any
/// other type is ignored. The booking is read when the event is handled, never taken from its payload, so events that arrive out of order give the same result.</item>
/// <item><b>Once</b>: the changes are idempotent (a cleaning already moved or cancelled is left as it is), so an event delivered again does nothing more; the "To do" of a
/// cleaning the host decides on is one while it is open.</item>
/// <item>Behind <c>Features:Routines</c> (the service reads and writes nothing with the module closed); the org is the event's (ADR-022-R6). A failure of the database is not
/// swallowed: the event is delivered again.</item>
/// </list>
/// </remarks>
public sealed class CleaningBookingChangesHandler(
    ICleaningAutomationService cleaning,
    ILogger<CleaningBookingChangesHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingModified or DomainEventType.BookingCancelled) || domainEvent.EntityId == Guid.Empty)
            return;

        var result = domainEvent.Type == DomainEventType.BookingModified
            ? await cleaning.FollowStayDatesAsync(domainEvent.OrgId, domainEvent.EntityId, ct)
            : await cleaning.FollowStayCancellationAsync(domainEvent.OrgId, domainEvent.EntityId, ct);

        if (result.Outcome is CleaningAutomationOutcome.Moved or CleaningAutomationOutcome.Cancelled or CleaningAutomationOutcome.HostToDecide)
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}) of org {OrgId}: the cleaning of booking {BookingId} is {Outcome}",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId,
                domainEvent.EntityId,
                result.Outcome);
        }
    }
}
