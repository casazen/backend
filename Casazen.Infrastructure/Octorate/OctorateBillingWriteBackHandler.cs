using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The billing data saved in CasaZen reaches Octorate when Octorate is the issuer of the documents (E06/F02/US01/T04, FIS-02 point 1, AC-NF82-1): the
/// consumer of <c>InvoiceDataSaved</c>. If <see cref="IDocumentIssuerService"/> says Octorate issues for the booking and the booking has a reservation
/// there, the data is written to the customer record of the booker (<see cref="IOctorateApiClient.UpsertBookerBillingDataAsync"/>), once for each change.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once for each change.</b> The data is read from the booking when the event is handled, not from the event (which carries no personal data,
/// ADR-022-R21), and hashed as it is written (<see cref="OctorateBillingMapping.HashOf"/>); the hash of the last successful write is kept on the invoice
/// data (<see cref="BookingInvoiceData.OctorateBillingHash"/>, encrypted). The same hash again, whether it is an event delivered again, two saves in a row or
/// a save that changed nothing Octorate keeps, writes nothing. The hash is stored after the call: a write whose answer was lost is made again, and the
/// call is a <c>PUT</c> of the same members.</item>
/// <item><b>No echo.</b> What came from Octorate (<see cref="InvoiceDataSource.Octorate"/>) is never sent back.</item>
/// <item><b>Never the host's problem.</b> The host's save committed the data and the event before this runs; a failure here is never an error shown to
/// it. A failing or refused call (no answer, a 5xx, the rate limit, a refusal of the credentials or of the guests' sensitive data before CasaZen's
/// outbound IPs are whitelisted, D24) is logged with ids and a code and rethrown, so the outbox delivers the event again and, after five deliveries,
/// keeps it dead-lettered with the code (AC-E06-F02-US01-03). A record Octorate does not know, a request it rejects, a conflict or no connection is
/// logged with ids and a code and left: the same answer five times would help nobody, and the data is written at the next save. No log carries a
/// name, a number or an address.</item>
/// <item><b>Not lost on the way.</b> A booking CasaZen creates on Octorate whose reservation is still being created, or whose confirmation is waiting in
/// the outbox, makes the event be delivered again after the create (<see cref="OctorateBillingPendingException"/>) instead of dropping the data. A booking
/// saved before it is confirmed, and one whose create failed, have no reservation to write to: the data is written at the next save.</item>
/// <item><b>Only where Octorate issues.</b> Any other issuer (CasaZen, the accountant, nobody) and an organization that has not chosen: nothing is sent.
/// Only the billing members are written, never a document or an Alloggiati datum (<see cref="OctorateBillingData"/>).</item>
/// <item>Nothing while <c>Features:OctorateConnector</c> or <c>Features:OctorateBillingSync</c> is off, and nothing for a property that is not mirrored.
/// The org is the event's (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class OctorateBillingWriteBackHandler(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IDocumentIssuerService issuer,
    IOctorateBookingTargets targets,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    TimeProvider timeProvider,
    ILogger<OctorateBillingWriteBackHandler> logger) : IEventHandler<DomainEvent>
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.InvoiceDataSaved || domainEvent.EntityId == Guid.Empty)
            return;

        if (!OctorateBillingSyncRules.IsOn(featureFlags))
            return;

        var payload = OctorateBillingSyncRules.PayloadOf<InvoiceDataSavedPayload>(domainEvent);
        if (payload is null)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} has a payload that is not an invoice data event: nothing is sent to Octorate",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        // No echo: what Octorate gave is not given back.
        if (payload.Source == InvoiceDataSource.Octorate)
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new { b.PropertyId, b.OctorateReservationId, b.Source })
            .FirstOrDefaultAsync(ct);
        if (booking is null)
            return;

        var decision = await issuer.ResolveIssuerAsync(bookingId, ct);
        if (decision.Kind != IssuerDecisionKind.Octorate)
            return;

        var accommodationId = await targets.AccommodationOfAsync(booking.PropertyId, ct);
        if (accommodationId is null)
            return;

        if (booking.OctorateReservationId is not { } reservationId)
        {
            // The reservation of a booking CasaZen creates on Octorate is not there yet: delivered again after the create, never dropped.
            if (await ReservationIsComingAsync(orgId, bookingId, booking.Source, ct))
                throw new OctorateBillingPendingException(bookingId);

            return;
        }

        var row = await db.BookingInvoiceData.FirstOrDefaultAsync(d => d.BookingId == bookingId && d.OrgId == orgId, ct);
        if (row is null)
            return;

        // Nothing about the recipient (only the guest's «yes»), or nothing Octorate takes (a person's name alone): an empty record is never sent.
        var data = OctorateBillingData.From(InvoiceDataView.Of(row));
        if (data is null || !OctorateBillingMapping.HasMembersToWrite(data))
            return;

        var hash = OctorateBillingMapping.HashOf(data);
        if (string.Equals(row.OctorateBillingHash, hash, StringComparison.Ordinal))
            return;

        using (orgScope.Use(orgId))
        {
            try
            {
                await client.UpsertBookerBillingDataAsync(accommodationId, reservationId, data, ct);
            }
            catch (OctorateException ex) when (OctorateBillingSyncRules.IsRetryable(ex))
            {
                logger.LogWarning(
                    "Billing data of booking {BookingId} of org {OrgId} not written to Octorate: {Code}; the event is delivered again",
                    bookingId,
                    orgId,
                    ex.Code);
                throw;
            }
            catch (OctorateException ex)
            {
                logger.LogWarning(
                    "Billing data of booking {BookingId} of org {OrgId} not written to Octorate: {Code} (HTTP {Status}); it is written at the next save of the invoice data",
                    bookingId,
                    orgId,
                    ex.Code,
                    ex.OctorateStatusCode);
                return;
            }
        }

        row.OctorateBillingHash = hash;
        row.OctorateBillingSentAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Billing data of booking {BookingId} of org {OrgId} written to Octorate", bookingId, orgId);
    }

    /// <summary>
    /// True when the reservation of a booking CasaZen creates on Octorate is on its way: its create is under way (the mirror row is <c>Pending</c>) or
    /// its confirmation is still waiting in the outbox (the create is that handler's work). Never for a booking of a channel: it arrives with its
    /// reservation. The same reading of «the create is coming» as the payments of a booking.
    /// </summary>
    private async Task<bool> ReservationIsComingAsync(Guid orgId, Guid bookingId, BookingSource source, CancellationToken ct)
    {
        if (FiscalCopy.IsOtaBookingSource(source))
            return false;

        var state = await db.OctorateBookingSyncs
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId)
            .Select(s => (OctorateSyncState?)s.State)
            .FirstOrDefaultAsync(ct);
        if (state == OctorateSyncState.Pending)
            return true;

        // Any other state is an outcome (synced without a reservation id cannot happen; failed and conflict are decided): nothing is coming.
        if (state is not null)
            return false;

        return await db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                e => e.OrgId == orgId
                     && e.EntityId == bookingId
                     && e.EventType == DomainEventType.BookingConfirmed
                     && e.DispatchedAt == null
                     && !e.DeadLettered,
                ct);
    }
}

/// <summary>
/// The invoice data of a booking whose reservation is still being created on Octorate, or is about to be (E06/F02/US01/T04): the event is delivered again
/// after the create, so the data is never dropped and never written twice.
/// </summary>
public sealed class OctorateBillingPendingException(Guid bookingId)
    : Exception($"The reservation of booking {bookingId} is not on Octorate yet: the billing data is written when the event is delivered again.");
