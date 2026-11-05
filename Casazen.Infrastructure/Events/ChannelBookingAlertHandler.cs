using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A booking that arrived from a channel is confirmed: pushes the host's devices the new-booking alert that a direct booking already
/// sends, with the name of the channel (<see cref="BookingNotifier.ChannelBookingAsync"/>, E08/F06/US01/T02, NF-79, AC-NF79-5).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which bookings</b>: the events <c>BookingCreated</c> and <c>BookingConfirmed</c> whose payload says the booking came from the
/// channel manager (<see cref="BookingEventOrigin.Octorate"/>, set by its inbound handlers) and is confirmed. A booking made in
/// CasaZen, by the host or by a guest, has its own alert (<see cref="BookingNotifier"/>) and never gets a second one from here.</item>
/// <item><b>Once per booking</b>: the push key is the booking (<see cref="PushDeliveryKeys.NewBooking"/>), the same as the direct
/// booking's, so the creation and the confirmation of one booking, and a redelivery of either (ADR-022-R7), are one push per device.</item>
/// <item><b>Recipients</b>: the hosts of the booking, as for a direct booking; when the per-user preferences of notifications (NF-65)
/// exist they decide the recipients and the channel. Push is on by default. No email, no guest email.</item>
/// <item>Ids, enums and dates only are read from the event (ADR-022-R21); the property, the dates and the channel are read from the
/// booking by the notifier, in its org (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class ChannelBookingAlertHandler(BookingNotifier notifier, ILogger<ChannelBookingAlertHandler> logger) : IEventHandler<DomainEvent>
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingCreated or DomainEventType.BookingConfirmed) || domainEvent.EntityId == Guid.Empty)
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

        if (payload is null)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not a booking event: no channel alert",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        if (payload.Origin != BookingEventOrigin.Octorate || payload.Status != BookingStatus.Confirmed)
            return;

        await notifier.ChannelBookingAsync(domainEvent.EntityId, ct);
    }
}
