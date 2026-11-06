using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// A booking CasaZen holds (a direct one, one the host entered) that the host edits is changed on Octorate after it (E08/F02/US01/T02, NF-75,
/// AC-NF75-1): the consumer of <c>BookingModified</c>, recorded with the edit in the same save, so the refund or the charge of the money has already been
/// decided when this runs. <b>CasaZen first, Octorate after</b>: the host's edit never waits for this push, and a failure of Octorate never undoes it.
/// It is also the consumer of the host's «ask again» (<c>OctorateSyncRequested</c>) for a change whose push failed or conflicted, with the host's choice
/// to keep CasaZen's version (<see cref="BookingEventPayload.Overwrite"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No echo.</b> A change that came from Octorate (<see cref="BookingEventOrigin.Octorate"/>: the import, the webhook) is never sent back, and a
/// booking of a channel is never written from here (its change is the channel's). Only what exists on Octorate: a booking with a reservation id and a row
/// of ours; every event while <c>Features:OctorateConnector</c> is off sends nothing.</item>
/// <item>The work is <see cref="IBookingOctorateSyncService.PushUpdateAsync"/>: a state the host sees (<c>Pending</c>, then <c>Synced</c>), a conflict kept
/// with both versions and never overwritten silently, a failure it lets through delivered again by the dispatcher.</item>
/// <item>Ids, enums, dates and the host's choice only are read from the event (ADR-022-R21); the org is the event's (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class OctorateBookingUpdateHandler(
    IBookingOctorateSyncService sync,
    IFeatureFlags featureFlags,
    ILogger<OctorateBookingUpdateHandler> logger) : IEventHandler<DomainEvent>
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type is not (DomainEventType.BookingModified or DomainEventType.OctorateSyncRequested) || domainEvent.EntityId == Guid.Empty)
            return;

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

        // A change that came from Octorate is never sent back, and a booking of a channel is changed on its channel.
        if (payload.Origin == BookingEventOrigin.Octorate || FiscalCopy.IsOtaBookingSource(payload.Source))
            return;

        await sync.PushUpdateAsync(
            domainEvent.EntityId,
            askedAgain: domainEvent.Type == DomainEventType.OctorateSyncRequested,
            overwrite: payload.Overwrite,
            ct);
    }
}
