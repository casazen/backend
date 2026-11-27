using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <c>CONTENT_NOTIFICATION</c> (E02/F03/US01/T07, ADR-023-R7, ADR-022): Octorate says something about the content of an accommodation (a publication
/// went through, a channel refused it, someone changed it). The notification is a signal and nothing more: the handler trusts nothing in it, takes
/// the org and the property from the confirmed mapping the webhook controller resolved the org from, and reads again what it needs through the
/// client, at once, instead of waiting for the next run of the job: the status of the publication (<see cref="IListingStatusService.SyncPropertyAsync"/>)
/// and the changes made outside CasaZen (<see cref="IOutsideChangeService.DetectAsync"/>, which never writes to Octorate).
/// </summary>
/// <remarks>
/// A listing that is no longer confirmed, or an org that disconnected, ends the handler quietly (nothing to retry). Any other failure throws, so the
/// event is delivered again (ADR-022-R4); both reads are idempotent. The log line names the event and the property, never the body of the call. Off
/// with <c>Features:OctorateConnector</c>.
/// </remarks>
public sealed class ContentNotificationHandler(
    AppDbContext db,
    IListingStatusService status,
    IOutsideChangeService outsideChanges,
    IFeatureFlags featureFlags,
    ILogger<ContentNotificationHandler> logger) : IEventHandler<OctorateWebhookReceivedEvent>
{
    public async Task HandleAsync(OctorateWebhookReceivedEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!string.Equals(domainEvent.WebhookType, OctorateWebhookEvents.ContentNotification, StringComparison.Ordinal))
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Octorate content notification {EventId}: the connector is off, nothing done", domainEvent.Id);
            return;
        }

        // The mapping the controller resolved the org from: it must still be confirmed, and still be that accommodation's.
        var mapping = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.Id == domainEvent.EntityId && m.OrgId == domainEvent.OrgId)
            .Select(m => new { m.PropertyId, m.OctorateAccommodationId, m.Status })
            .FirstOrDefaultAsync(ct);
        if (mapping is null
            || mapping.Status != OctorateMappingStatus.Confirmed
            || mapping.PropertyId is not { } propertyId
            || !string.Equals(mapping.OctorateAccommodationId, domainEvent.AccommodationId, StringComparison.Ordinal))
        {
            logger.LogInformation("Octorate content notification {EventId}: its listing is no longer confirmed to a property, nothing done", domainEvent.Id);
            return;
        }

        try
        {
            await status.SyncPropertyAsync(domainEvent.OrgId, propertyId, ct);
            await outsideChanges.DetectAsync(domainEvent.OrgId, propertyId, ct);
        }
        catch (OctorateAuthException ex) when (ex.IsNotConnected)
        {
            // The org disconnected after the notification: there is nothing to read, and a retry would find the same.
            logger.LogInformation("Octorate content notification {EventId}: the org is no longer connected, nothing done", domainEvent.Id);
            return;
        }

        logger.LogInformation("Octorate content notification handled: event {EventId}, property {PropertyId}", domainEvent.Id, propertyId);
    }
}
