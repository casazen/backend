using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// A booking arrived from Octorate: reads the contact of its guest right away, so the messages, the check-in and the invoice do not wait for the
/// first flow that asks for it (E07/F06/US02/T02, NF-94, ADR-022). It only shortens the wait: the flows that need the contact (E05, E03, E06) call
/// <see cref="IOctorateContactsService"/> themselves when they need it, whether or not this event was delivered.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which events</b>: <c>BookingCreated</c> whose payload says the booking came from the channel manager
/// (<see cref="BookingEventOrigin.Octorate"/>, set by the import). A booking made in CasaZen, by the host or by a guest, already has its guest's
/// contact and is never read from Octorate here. The event carries ids and enums only (ADR-022-R21); the booking is read in its org.</item>
/// <item><b>The service decides</b> whether anything is read at all (<c>Crm:OctorateContactsEnabled</c>, the cap per hour, a stay already over):
/// this handler checks nothing of that and logs nothing while the service is off.</item>
/// <item><b>A failure never fails the event</b>: it is logged with the ids and the type of the error (never a message that could hold a value)
/// and swallowed, so the bus does not deliver the event again and the other handlers are not blamed.</item>
/// <item><b>Idempotent</b> (ADR-022-R7): a booking read in the last day is not read again, so an event delivered twice reads once.</item>
/// </list>
/// </remarks>
public sealed class OctorateContactsEventHandler(IOctorateContactsService service, ILogger<OctorateContactsEventHandler> logger)
    : IEventHandler<DomainEvent>
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.BookingCreated || domainEvent.EntityId == Guid.Empty)
            return;

        BookingEventPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BookingEventPayload>(domainEvent.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || payload.Origin != BookingEventOrigin.Octorate)
            return;

        try
        {
            var result = await service.ReadForBookingAsync(domainEvent.OrgId, domainEvent.EntityId, ct);
            logger.LogDebug(
                "Octorate contact of booking {BookingId} of org {OrgId} on event {EventId}: {Outcome}",
                domainEvent.EntityId,
                domainEvent.OrgId,
                domainEvent.Id,
                result.Outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Octorate contact of booking {BookingId} of org {OrgId} failed on event {EventId}: {Error}; the flows that need it ask again",
                domainEvent.EntityId,
                domainEvent.OrgId,
                domainEvent.Id,
                CrmDatabaseErrors.Describe(ex));
        }
    }
}
