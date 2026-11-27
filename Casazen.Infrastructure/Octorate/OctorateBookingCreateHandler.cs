using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// A booking confirmed in CasaZen is created on Octorate (E08/F01/US01/T03, AC-NF74-3, AC-NF74-5): the consumer of <c>BookingConfirmed</c>, so the
/// host wizard, the phone entry and the booking site follow the same path, and of <c>OctorateSyncRequested</c>, the retry the host asks for.
/// The work is <see cref="IBookingOctorateSyncService.PushCreateAsync"/>; a failure it lets through is delivered again by the dispatcher.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No echo.</b> A confirmation recorded by the import of Octorate (<see cref="BookingEventOrigin.Octorate"/>) and a booking of a channel
/// (Airbnb, Booking.com, Expedia, Vrbo, TripAdvisor, Agoda, another portal) are never written back; the service checks the booking again.</item>
/// <item>Nothing while <c>Features:OctorateConnector</c> is off.</item>
/// <item>Ids, enums and dates only are read from the event (ADR-022-R21); the org is the event's (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class OctorateBookingCreateHandler(
    IBookingOctorateSyncService sync,
    IFeatureFlags featureFlags,
    ILogger<OctorateBookingCreateHandler> logger) : IEventHandler<DomainEvent>
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingConfirmed or DomainEventType.OctorateSyncRequested)
            || domainEvent.EntityId == Guid.Empty)
        {
            return;
        }

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
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
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not a booking event: nothing is sent to Octorate",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        if (payload.Origin == BookingEventOrigin.Octorate || FiscalCopy.IsOtaBookingSource(payload.Source))
            return;

        await sync.PushCreateAsync(domainEvent.EntityId, ct);
    }
}
