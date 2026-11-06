using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctorateContactsService"/> (E07/F06/US02/T02, NF-94, CRM-07, ADR-022): the contact of the guest of ONE Octorate booking, read
/// when someone needs it and stored as contacts of the CRM. No Octorate code is mocked here: the operation is the real one of
/// <see cref="IOctorateApiClient.GetGuestContactInfoAsync"/>, which only answers from the static IP addresses registered with Octorate.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Gates, in this order</b>: <c>Crm:OctorateContactsEnabled</c> and the connector flag (nothing is queried when they are off); the CRM
/// module open (<see cref="CrmProfileSyncGate"/>: <c>Features:CrmMarketing</c> on and <c>Crm:ContactIndexKey</c> set, else nothing could be
/// stored and nothing is asked of Octorate); a booking of
/// the org with an Octorate reservation id that came from Octorate (not one CasaZen made and sent there, which has a row of the mirror);
/// a check-out that is not in the past; a guest that was not erased; what the memory of
/// <see cref="OctorateContactReadLedger"/> says (read lately, or refused lately: no call); a confirmed listing of the property; and at most
/// <c>Crm:OctorateContactReadsPerHour</c> reads an hour for the org. Only then Octorate is called: the reservation (for the guest id) and the
/// contact of that guest, two calls for one booking, never a list.</item>
/// <item><b>Tenant</b>: every query names the org on top of the tenant filter; the calls act for the org (<see cref="IOctorateOrgScope.Use"/>).</item>
/// <item><b>What is stored</b>: the address and the number go through <see cref="ICrmContactService.UpsertContactAsync"/>, with origin
/// <see cref="GuestContactOrigin.OctorateContactInfo"/>; the basis is that of <see cref="CrmContactRules.ResolveBasis"/>, which is service only. The
/// OTA forwards are told by the contact service (the source of the booking, the relay domains). A customer of the booking engine of the agency
/// (channel <c>270</c>, to confirm in the sandbox) brings the notice of its booking only while <c>Crm:BookingEngineSoftOptIn</c> is on, and a
/// confirmed booking makes the address soft opt-in then; otherwise service only.</item>
/// <item><b>A refusal</b> (401/403: no static IP, no permission), a request Octorate rejects (a 4xx that is no missing reservation and no limit),
/// a reservation Octorate no longer knows and a reservation with no guest id are remembered as not available for the booking; <b>an answer that is
/// not understood</b> (no member of a contact in it, or empty while the reservation says there is an address or a number) is remembered as
/// unrecognized, never as a guest with no contact; an outage or a limit is not remembered (a later call tries again) and is never retried here.</item>
/// <item><b>Privacy</b>: the logs carry ids, counts and the outcome, never an address, a number, a name or a body.</item>
/// </list>
/// </remarks>
public sealed class OctorateContactsService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    ICrmContactService contacts,
    ICrmContactIndex contactIndex,
    OctorateContactReadLedger ledger,
    IOptions<CrmOptions> options,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<OctorateContactsService> logger) : IOctorateContactsService
{
    /// <summary>
    /// The version recorded as the soft opt-in notice of a customer of the booking engine of the agency: the notice is the agency's, shown by its
    /// booking engine, and its being shown is the product owner's declaration (<c>Crm:BookingEngineSoftOptIn</c>, D34), not a text CasaZen holds.
    /// </summary>
    public const string BookingEngineNoticeVersion = "octorate-booking-engine";

    /// <summary>
    /// The code the log carries when the answer of <c>contactInfo</c> is not understood (<see cref="OctorateContactReadOutcome.Unrecognized"/>): the code
    /// only, never a member or a value of the answer.
    /// </summary>
    public const string UnrecognizedAnswerCode = "octorate_contact_info_unrecognized";

    public async Task<OctorateContactReadResult> ReadForBookingAsync(Guid orgId, Guid bookingId, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.OctorateContactsEnabled || !featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new OctorateContactReadResult(OctorateContactReadOutcome.Disabled);

        // What is read is stored by the CRM, and the CRM stores nothing until its module is open (Features:CrmMarketing on and Crm:ContactIndexKey
        // set: CrmProfileSyncGate, the one rule of the sync, the job and the demo seed). With it closed the contact could not be kept, so it is not asked
        // of Octorate either: a sensitive read that is dropped on the floor, and made again at the next call, is the worst of both.
        if (!CrmProfileSyncGate.IsOpen(featureFlags, contactIndex))
            return new OctorateContactReadResult(OctorateContactReadOutcome.Disabled);

        if (orgId == Guid.Empty || bookingId == Guid.Empty)
            return new OctorateContactReadResult(OctorateContactReadOutcome.NotAnOctorateBooking);

        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .Select(b => new BookingFacts(b.PropertyId, b.GuestId, b.OctorateReservationId, b.Source, b.Status, b.CheckOutDate, b.CreatedAt))
            .FirstOrDefaultAsync(ct);
        if (booking is null || string.IsNullOrEmpty(booking.OctorateReservationId))
            return new OctorateContactReadResult(OctorateContactReadOutcome.NotAnOctorateBooking);

        // A booking made in CasaZen and sent to Octorate has a reservation id too, and a row of the mirror (E08/F01): the row is made when CasaZen creates the
        // reservation, never by the import. Its guest's contact is CasaZen's own, and the channel it is written on (the direct one, 270,
        // Octorate__DirectChannelId) is also the one of the booking engine of the agency: it must not pass for a customer of that engine and be given its soft opt-in.
        var sentByCasaZen = await db.OctorateBookingSyncs
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(s => s.OrgId == orgId && s.BookingId == bookingId, ct);
        if (sentByCasaZen)
            return new OctorateContactReadResult(OctorateContactReadOutcome.NotAnOctorateBooking);

        // No history: a stay that is over has no use for the contact of its guest.
        if (booking.CheckOutDate.Date < timeProvider.TodayInRome().Date)
            return new OctorateContactReadResult(OctorateContactReadOutcome.StayOver);

        // The guest of an erased or anonymized record is not asked about: its contacts were erased (E07/F06/US02/T01) and must not come back.
        var guest = await db.Guests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(g => g.OrgId == orgId && g.Id == booking.GuestId)
            .Select(g => new GuestFacts(g.GuestCrmProfileId, g.FirstName, g.LastName, g.DataAnonymizedDate != null || g.IsDeleted || g.ErasureRequested))
            .FirstOrDefaultAsync(ct);
        if (guest is null || guest.Erased)
            return new OctorateContactReadResult(OctorateContactReadOutcome.GuestErased);

        var now = timeProvider.GetUtcNow();
        if (ledger.Recall(orgId, bookingId, now) is { } remembered)
            return new OctorateContactReadResult(remembered);

        var accommodationId = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.PropertyId == booking.PropertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
        if (accommodationId is null)
            return new OctorateContactReadResult(OctorateContactReadOutcome.NotMapped);

        if (!ledger.TryTakeRead(orgId, now, settings.OctorateContactReadsPerHour))
        {
            logger.LogInformation(
                "Octorate contact of booking {BookingId} of org {OrgId} not read: the {PerHour} reads an hour of the org are used",
                bookingId,
                orgId,
                settings.OctorateContactReadsPerHour);
            return new OctorateContactReadResult(OctorateContactReadOutcome.OverCap);
        }

        OctorateReservation reservation;
        OctorateGuestContactInfo info;
        try
        {
            using (orgScope.Use(orgId))
            {
                reservation = await client.GetReservationAsync(accommodationId, booking.OctorateReservationId, ct);
                if (string.IsNullOrEmpty(reservation.GuestId))
                    return NotAvailable(orgId, bookingId, now, "the reservation names no guest");

                // Octorate says in the reservation whether it holds an address or a number for the guest: with neither there is nothing to ask,
                // and a sensitive read that gives nothing is not made.
                if (reservation.GuestEmailAvailable == false && reservation.GuestPhoneAvailable == false)
                {
                    ledger.RememberRead(orgId, bookingId, hadContact: false, now);
                    logger.LogInformation("Octorate contact of booking {BookingId} of org {OrgId}: Octorate holds none for the guest", bookingId, orgId);
                    return new OctorateContactReadResult(OctorateContactReadOutcome.NothingToStore);
                }

                info = await client.GetGuestContactInfoAsync(accommodationId, reservation.GuestId, ct);
            }
        }
        catch (OctorateSensitiveDataNotAllowedException)
        {
            return NotAvailable(orgId, bookingId, now, "Octorate does not release the contacts to this application yet");
        }
        catch (OctorateNotFoundException)
        {
            return NotAvailable(orgId, bookingId, now, "Octorate no longer knows the reservation");
        }
        catch (OctorateException ex) when (ex is OctorateRequestRejectedException or OctorateConflictException)
        {
            // Octorate refused the request itself (a 4xx that is no refusal of the data, no missing reservation and no limit): sent again it is refused
            // again, so it is remembered like a refusal and not sent in a loop. Only the code of the error is logged.
            return NotAvailable(orgId, bookingId, now, $"Octorate rejected the request ({ex.Code})");
        }
        catch (OctorateException ex)
        {
            // An outage, the limit, a connection that is not usable: not the booking's fault, so nothing is remembered about it. Not retried here.
            logger.LogWarning(
                "Octorate contact of booking {BookingId} of org {OrgId} could not be read now ({Error}): a later call tries again",
                bookingId,
                orgId,
                ex.Code);
            return new OctorateContactReadResult(OctorateContactReadOutcome.Deferred);
        }

        // An empty answer is "the guest has no contact" only when CasaZen understood it and Octorate did not say it holds an address or a number. With none
        // of the members of a contact in it, or empty while the reservation says there is an address or a number, it is an answer that is not understood (the
        // shape of contactInfo is not proven by the sandbox): said so, remembered, and never taken for a guest with no contact. Only the code is logged.
        if (info.IsEmpty && (!info.Recognized || reservation.GuestEmailAvailable == true || reservation.GuestPhoneAvailable == true))
        {
            ledger.RememberUnrecognized(orgId, bookingId, now);
            logger.LogWarning(
                "Octorate contact of booking {BookingId} of org {OrgId}: the answer is not understood ({Code}): not asked again for {Hours} hours",
                bookingId,
                orgId,
                UnrecognizedAnswerCode,
                OctorateContactReadLedger.NotAvailableMemory.TotalHours);
            return new OctorateContactReadResult(OctorateContactReadOutcome.Unrecognized);
        }

        var stored = await StoreAsync(orgId, booking, guest, reservation, info, settings, ct);
        ledger.RememberRead(orgId, bookingId, stored > 0, now);
        logger.LogInformation("Octorate contact of booking {BookingId} of org {OrgId} read: {Stored} contacts stored", bookingId, orgId, stored);
        return new OctorateContactReadResult(
            stored > 0 ? OctorateContactReadOutcome.Stored : OctorateContactReadOutcome.NothingToStore,
            stored);
    }

    /// <summary>
    /// Stores the address and the number as contacts of the guest of the booking and returns how many were stored. The contact service decides
    /// what each one is: a forward address of an OTA stays service only whatever is passed here.
    /// </summary>
    private async Task<int> StoreAsync(
        Guid orgId,
        BookingFacts booking,
        GuestFacts guest,
        OctorateReservation reservation,
        OctorateGuestContactInfo info,
        CrmOptions settings,
        CancellationToken ct)
    {
        var displayName = $"{guest.FirstName} {guest.LastName}".Trim();
        var purchase = CrmContactRules.IsPurchase(booking.Status);

        // The customer of the booking engine of the agency saw the agency's notice when booking (declared by the product owner); everyone else
        // brings none, and so is service only.
        CrmBasisEvidence? notice = null;
        if (settings.BookingEngineSoftOptIn
            && string.Equals(reservation.ChannelId, BookingStateMapper.BookingEngineChannelId, StringComparison.Ordinal))
        {
            notice = new CrmBasisEvidence(
                BookingEngineNoticeVersion,
                DateTime.SpecifyKind(booking.CreatedAt, DateTimeKind.Utc),
                GuestConsentSource.BookingCheckout);
        }

        var stored = 0;
        if (!string.IsNullOrWhiteSpace(info.Email))
        {
            var email = await contacts.UpsertContactAsync(
                new CrmContactInput(
                    orgId,
                    GuestContactChannel.Email,
                    info.Email,
                    GuestContactOrigin.OctorateContactInfo,
                    GuestCrmProfileId: guest.GuestCrmProfileId,
                    GuestId: booking.GuestId,
                    DisplayName: displayName,
                    BookedVia: booking.Source,
                    Notice: notice,
                    PurchaseConfirmed: purchase),
                ct);
            if (email is not null)
                stored++;
        }

        if (!string.IsNullOrWhiteSpace(info.Phone))
        {
            var phone = await contacts.UpsertContactAsync(
                new CrmContactInput(
                    orgId,
                    GuestContactChannel.Phone,
                    info.Phone,
                    GuestContactOrigin.OctorateContactInfo,
                    GuestCrmProfileId: guest.GuestCrmProfileId,
                    GuestId: booking.GuestId,
                    DisplayName: displayName,
                    BookedVia: booking.Source,
                    PurchaseConfirmed: purchase),
                ct);
            if (phone is not null)
                stored++;
        }

        return stored;
    }

    private OctorateContactReadResult NotAvailable(Guid orgId, Guid bookingId, DateTimeOffset now, string reason)
    {
        ledger.RememberNotAvailable(orgId, bookingId, now);
        logger.LogInformation(
            "Octorate contact of booking {BookingId} of org {OrgId} is not available ({Reason}): not asked again for {Hours} hours",
            bookingId,
            orgId,
            reason,
            OctorateContactReadLedger.NotAvailableMemory.TotalHours);
        return new OctorateContactReadResult(OctorateContactReadOutcome.NotAvailable);
    }

    /// <summary>The columns of the guest of the booking the read needs: its profile, its name for the contacts, and whether it was erased.</summary>
    private sealed record GuestFacts(Guid? GuestCrmProfileId, string FirstName, string LastName, bool Erased);

    /// <summary>The columns of the booking the read needs, nothing else.</summary>
    private sealed record BookingFacts(
        Guid PropertyId,
        Guid GuestId,
        string? OctorateReservationId,
        BookingSource Source,
        BookingStatus Status,
        DateTime CheckOutDate,
        DateTime CreatedAt);
}
