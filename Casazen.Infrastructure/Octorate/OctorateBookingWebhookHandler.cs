using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the three handlers of the reservation notifications of Octorate share (E02/F02/US01/T02, ADR-031, ADR-022): the notification is a
/// signal and nothing more, so the handler trusts nothing in it, takes the org and the property from the confirmed mapping the webhook
/// controller resolved the org from, and re-reads the reservations through <see cref="IBookingImportService"/> before any effect. It runs
/// with no request and no EF tenant filter: the org is the one of the event (<see cref="DomainEvent.OrgId"/>), opened with
/// <see cref="IOctorateOrgScope.Use"/> for the calls to Octorate, which take no org.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once, in order.</b> A repeated, late or out-of-order notification changes nothing: the service applies a reservation only when its
/// <c>updateTime</c> is newer than the one applied, and records its events once (their keys). There is no table of received events: Octorate
/// gives no event id.</item>
/// <item><b>What is re-read.</b> The ids in the body are not relied on (the content of the sandbox's <c>ping</c> is not recorded): the reservations
/// of the property changed since two days before the notification arrived are read, which includes the one the notification is about. Octorate also
/// notifies CasaZen's own writes; re-reading them finds nothing to change, and nothing here sends to Octorate, so there is no echo.</item>
/// <item><b>Failures.</b> A listing that is no longer confirmed, or an org that disconnected, ends the handler quietly (nothing to retry). A run
/// that stopped before the end (the rate limit of the org) or an error of Octorate throws, so the dispatcher delivers the event again (ADR-022-R4).
/// The reconciliation catches whatever is left.</item>
/// <item><b>Privacy.</b> The log line is <c>{eventId, eventType, processed}</c>: never the body of the call.</item>
/// <item>Off with <c>Features:OctorateConnector</c>: the connector is switched off, no call is made.</item>
/// </list>
/// </remarks>
public abstract class OctorateBookingWebhookHandler(
    AppDbContext db,
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger logger) : IEventHandler<OctorateWebhookReceivedEvent>
{
    /// <summary>The Octorate event names (<see cref="OctorateWebhookEvents"/>) this handler deals with.</summary>
    protected abstract IReadOnlyCollection<string> WebhookTypes { get; }

    public async Task HandleAsync(OctorateWebhookReceivedEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!WebhookTypes.Contains(domainEvent.WebhookType, StringComparer.Ordinal))
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Octorate webhook {EventId} ({EventType}): the connector is off, nothing done", domainEvent.Id, domainEvent.WebhookType);
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
            logger.LogInformation(
                "Octorate webhook {EventId} ({EventType}): its listing is no longer confirmed to a property, nothing done", domainEvent.Id, domainEvent.WebhookType);
            return;
        }

        ImportResult result;
        try
        {
            using (orgScope.Use(domainEvent.OrgId))
                result = await import.ImportIncrementalAsync(propertyId, domainEvent.ReceivedAt - OctorateBookingRuns.Overlap, ct);
        }
        catch (OctorateAuthException ex) when (ex.IsNotConnected)
        {
            // The org disconnected after the notification: there is nothing to read, and a retry would find the same.
            logger.LogInformation("Octorate webhook {EventId} ({EventType}): the org is no longer connected, nothing done", domainEvent.Id, domainEvent.WebhookType);
            return;
        }

        if (result.Outcome is ImportOutcome.Incomplete)
            throw new BookingImportIncompleteException(domainEvent.Id);

        logger.LogInformation("Octorate webhook handled: event {EventId}, type {EventType}, processed {Processed}", domainEvent.Id, domainEvent.WebhookType, true);
    }
}

/// <summary>A run triggered by a notification stopped before it read everything it had to: the event is delivered again (ids only in the message).</summary>
public sealed class BookingImportIncompleteException(Guid eventId)
    : Exception($"The re-read of the reservations for the Octorate notification {eventId} stopped before the end; it is delivered again");

/// <summary>
/// <c>RESERVATION_CREATED</c> and <c>RESERVATION_CONFIRMED</c> (E02/F02/US01/T02): a new reservation, or one that was waiting and is now confirmed.
/// The events <c>BookingCreated</c> and <c>BookingConfirmed</c> are recorded by the service with <c>Origin = Octorate</c>, not here: one change
/// records one event.
/// </summary>
public sealed class BookingCreatedHandler(
    AppDbContext db,
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger<BookingCreatedHandler> logger)
    : OctorateBookingWebhookHandler(db, import, orgScope, featureFlags, logger)
{
    private static readonly string[] Types = [OctorateWebhookEvents.ReservationCreated, OctorateWebhookEvents.ReservationConfirmed];

    protected override IReadOnlyCollection<string> WebhookTypes => Types;
}

/// <summary>
/// <c>RESERVATION_CHANGE</c> (E02/F02/US01/T02): the dates, the guests, the payment or the state of a reservation changed. The service finds
/// what changed, applies the version if it is newer than the one held, and records <c>BookingModified</c>. Check-in and check-out are never
/// read from here (Octorate sends none); an inbound change never sends anything back.
/// </summary>
public sealed class BookingModifiedHandler(
    AppDbContext db,
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger<BookingModifiedHandler> logger)
    : OctorateBookingWebhookHandler(db, import, orgScope, featureFlags, logger)
{
    private static readonly string[] Types = [OctorateWebhookEvents.ReservationChange];

    protected override IReadOnlyCollection<string> WebhookTypes => Types;
}

/// <summary>
/// <c>RESERVATION_CANCELLED</c> (E02/F02/US01/T02): the booking becomes cancelled and <c>BookingCancelled</c> is recorded. The amounts of the
/// cancelled reservation are re-read like any other: what the guest paid and what the channel kept (a cancellation fee included) are stored as
/// Octorate gives them, never reset by the cancellation.
/// </summary>
public sealed class BookingCancelledHandler(
    AppDbContext db,
    IBookingImportService import,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger<BookingCancelledHandler> logger)
    : OctorateBookingWebhookHandler(db, import, orgScope, featureFlags, logger)
{
    private static readonly string[] Types = [OctorateWebhookEvents.ReservationCancelled];

    protected override IReadOnlyCollection<string> WebhookTypes => Types;
}
