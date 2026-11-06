using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The invoice data of a booking imported from Octorate is pre-filled from the customer record of its booker (E06/F02/US01/T04, FIS-01, AC-NF81-2): the
/// consumer of <c>BookingCreated</c> recorded by the import (origin Octorate). It reads the billing data of the reservation
/// (<see cref="IOctorateApiClient.GetBookerBillingDataAsync"/>) and saves it as invoice data with the source <see cref="InvoiceDataSource.Octorate"/>,
/// the lowest: it fills only what is empty and never overwrites or clears what the host or the guest wrote.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Never the import's problem.</b> The import has committed the booking and the event before this runs; whatever goes wrong here leaves a booking
/// with no data from Octorate, never a failed import. Nothing that goes wrong in the call to Octorate is swallowed: a failing or refused call (no answer, a
/// 5xx, the rate limit, a refusal of the credentials or of the guests' sensitive data before CasaZen's outbound IPs are whitelisted, D24, a record or a
/// reservation Octorate does not know, a request it rejects, a conflict, the wrong environment, no connection) is logged with ids and a code and
/// rethrown, so the outbox delivers the event again and, after five deliveries, keeps it dead-lettered with the code (AC-E06-F02-US01-03): the booking
/// may have no data at all, so there is no row of its own to keep the code on. Only data that fails the checks of the invoice data as a whole once put
/// together with what is stored (a refusal of our own rules, not of a call) is logged with ids and a code and left. No log carries a name, a number or
/// an address.</item>
/// <item><b>Only what is valid.</b> A field of Octorate that fails the checks of the invoice data (a fiscal code that does not check out, a province
/// written in full) is left out and the rest is saved; the host sees the field empty.</item>
/// <item><b>Idempotent.</b> A save that fills nothing writes nothing and records no event, so an event delivered again changes nothing; the save by
/// this handler is of the source Octorate, which the write-back (<see cref="OctorateBillingWriteBackHandler"/>) never sends back.</item>
/// <item><b>No history.</b> A stay that is over, or a booking that was cancelled since, is not read: the sensitive data of a guest is not fetched in bulk
/// for stays nobody will invoice from this flow, and the quota of the org stays for the live bookings. (The import itself records <c>BookingCreated</c>
/// only for a stay that is not over and not cancelled; this is the same rule when the event is handled later.)</item>
/// <item>Nothing while <c>Features:OctorateConnector</c> or <c>Features:OctorateBillingSync</c> is off, nothing for a property that is not mirrored, and
/// nothing for a booking CasaZen created itself (it has no customer record on Octorate to read). The org is the event's (ADR-022-R6).</item>
/// </list>
/// </remarks>
public sealed class InvoiceDataFromOctorateHandler(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IOctorateBookingTargets targets,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IInvoiceDataService invoiceData,
    TimeProvider timeProvider,
    ILogger<InvoiceDataFromOctorateHandler> logger) : IEventHandler<DomainEvent>
{
    /// <summary>How many times the fields that fail a check are dropped and the rest checked again (dropping the country can change what the postal code needs).</summary>
    private const int MaxCleaningPasses = 3;

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.BookingCreated || domainEvent.EntityId == Guid.Empty)
            return;

        if (!OctorateBillingSyncRules.IsOn(featureFlags))
            return;

        // Only what the import of Octorate created: the origin says it, the booking itself is checked below.
        if (OctorateBillingSyncRules.PayloadOf<BookingEventPayload>(domainEvent) is not { Origin: BookingEventOrigin.Octorate })
            return;

        var orgId = domainEvent.OrgId;
        var bookingId = domainEvent.EntityId;
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new { b.PropertyId, b.OctorateReservationId, b.CheckOutDate, b.Status })
            .FirstOrDefaultAsync(ct);
        if (booking?.OctorateReservationId is not { } reservationId)
            return;

        // No history, and nothing for a booking that was cancelled since: its guest's data is not fetched.
        if (booking.Status == BookingStatus.Cancelled || booking.CheckOutDate < timeProvider.TodayInRome())
            return;

        // A property that is not mirrored has no accommodation on Octorate: there is nothing to read.
        var accommodationId = await targets.AccommodationOfAsync(booking.PropertyId, ct);
        if (accommodationId is null)
            return;

        OctorateBillingData? billing;
        using (orgScope.Use(orgId))
        {
            try
            {
                billing = await client.GetBookerBillingDataAsync(accommodationId, reservationId, ct);
            }
            catch (OctorateException ex)
            {
                // Nothing is swallowed: the booking has no row to keep a code on (it may have no data at all), so the outbox keeps the failure, with its
                // code, as it keeps every other: the event is delivered again and, after five deliveries, dead-lettered.
                logger.LogWarning(
                    "Billing data of booking {BookingId} of org {OrgId} not read from Octorate: {Code} (HTTP {Status}); the event is delivered again",
                    bookingId,
                    orgId,
                    ex.Code,
                    ex.OctorateStatusCode);
                throw;
            }
        }

        if (billing is not { HasContent: true })
            return;

        var command = Cleaned(billing.ToInvoiceData(), out var dropped);
        if (dropped.Count > 0)
        {
            logger.LogInformation(
                "Billing data of booking {BookingId} of org {OrgId} from Octorate: {Count} field(s) left out because they fail the invoice data checks ({Fields})",
                bookingId,
                orgId,
                dropped.Count,
                string.Join(", ", dropped));
        }

        if (command is null)
            return;

        try
        {
            await invoiceData.SaveAsync(bookingId, command, InvoiceDataSource.Octorate, updatedByUserId: null, save: true, ct: ct);
        }
        catch (DomainRuleException ex)
        {
            // The data of Octorate fails a check once put together with what is stored: the booking simply has no data from Octorate.
            logger.LogWarning(
                "Billing data of booking {BookingId} of org {OrgId} from Octorate not saved: {Code}",
                bookingId,
                orgId,
                ex.Code);
        }
    }

    /// <summary>
    /// <paramref name="command"/> normalized and checked field by field as the invoice data checks it, with every field that fails left out (listed in
    /// <paramref name="dropped"/> by name, never by value); <c>null</c> when nothing is left to save.
    /// </summary>
    internal static SaveInvoiceDataCommand? Cleaned(SaveInvoiceDataCommand command, out IReadOnlyList<string> dropped)
    {
        var removed = new List<string>();
        var current = InvoiceDataFormats.Normalize(command, InvoiceDataFormats.EffectiveCountry(command, storedCountry: null));
        for (var pass = 0; pass < MaxCleaningPasses; pass++)
        {
            var failures = InvoiceDataFormats.Validate(current, requireIdentification: false);
            if (failures.Count == 0)
                break;

            foreach (var field in failures.Select(f => f.Field).Distinct())
            {
                removed.Add(field);
                current = Without(current, field);
            }
        }

        dropped = removed;
        var hasText = new[]
        {
            current.Name, current.FiscalCode, current.VatNumber, current.AddressStreet, current.AddressCivic, current.PostalCode, current.City,
            current.Province, current.SdiRecipientCode, current.Pec,
        }.Any(text => !string.IsNullOrWhiteSpace(text));

        // The kind and the country alone are not data: they only qualify what is sent with them.
        return hasText ? current : null;
    }

    private static SaveInvoiceDataCommand Without(SaveInvoiceDataCommand command, string field) => field switch
    {
        InvoiceDataFields.RecipientKind => command with { RecipientKind = null },
        InvoiceDataFields.Name => command with { Name = null },
        InvoiceDataFields.FiscalCode => command with { FiscalCode = null },
        InvoiceDataFields.VatNumber => command with { VatNumber = null },
        InvoiceDataFields.AddressStreet => command with { AddressStreet = null },
        InvoiceDataFields.AddressCivic => command with { AddressCivic = null },
        InvoiceDataFields.PostalCode => command with { PostalCode = null },
        InvoiceDataFields.City => command with { City = null },
        InvoiceDataFields.Province => command with { Province = null },
        InvoiceDataFields.CountryCode => command with { CountryCode = null },
        InvoiceDataFields.SdiRecipientCode => command with { SdiRecipientCode = null },
        InvoiceDataFields.Pec => command with { Pec = null },
        _ => command,
    };
}
