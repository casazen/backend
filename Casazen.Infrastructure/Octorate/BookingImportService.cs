using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IBookingImportService"/> (E02/F02/US01/T01, ADR-031, ADR-033): one <see cref="Booking"/> for each reservation of the property's
/// room on Octorate, kept up to date, and CasaZen's own states sent back.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant.</b> The org is the one of the confirmed <see cref="OctorateListingMapping"/> of the property; every query names it, because a
/// job and an event handler run with no EF tenant filter. The caller opens <c>IOctorateOrgScope.Use(orgId)</c> only for the calls it makes
/// itself; the service opens it for the client calls it makes.</item>
/// <item><b>What is read.</b> The list of the room's reservations changed since a day (<c>MODIFIED_BETWEEN</c>, 200 asked for a page, page after page
/// until a page with no item: a server may serve fewer than the size asked for, so a short page is not the end), and the single read of each one
/// that is new or newer than what CasaZen holds (the list has no payments nor price breakdown). A reservation of another room of the listing is not
/// imported and is reported. An item of the list that cannot be read counts as failed: the run is not complete and the next one reads it again.</item>
/// <item><b>Once.</b> The key is (org, Octorate reservation id), unique in the database; each reservation is applied under an advisory lock of
/// that key, so the job and a webhook handler never make it twice. A version whose <c>updateTime</c> is not newer than the applied one changes
/// nothing; an unchanged reservation is not even read again.</item>
/// <item><b>The booking follows its room.</b> A reservation is found by (org, Octorate id). When a newer version of it is read from the room of
/// another property of the org, the booking is moved to that property (a modification is recorded), so its nights are not held on the old one.</item>
/// <item><b>Its own echo.</b> A reservation CasaZen created has the booking code as <c>refer</c>. If it is read before the push saved its id, the booking
/// of the same org and property with that code and no id is linked to it (id and version) and no second booking is made (E08/F01/US01).</item>
/// <item><b>Events.</b> Recorded with <c>Origin = Octorate</c> in the same unit of work as the change (ids, enums and dates only), through
/// <see cref="BookingEvents"/> and its keys. A stay that is already over, and a reservation first seen already cancelled, are stored for the
/// record and raise none: nothing is to be told, scheduled or cleaned for them.</item>
/// <item><b>Money.</b> The figures of a booking CasaZen owns (entered by the host, or paid through CasaZen) are not overwritten by the read of its
/// reservation, and a booking CasaZen cancelled is not brought back by it. The OTA figures are never derived: see <see cref="BookingStateMapper"/>.
/// A price Octorate does not give is not written (never replaced by a zero), and a reservation that does not state the currency of the property
/// brings no amount at all: the figures the booking held stay.</item>
/// <item><b>Who collects (E08/F03/US03/T02, NF-77).</b> A booking of a channel (an OTA source) gets <see cref="Booking.OtaCompanyCollect"/> from the
/// <c>companyCollect</c> of the reservation and <see cref="Booking.OtaCommissionIncludedInPayments"/> from the portal catalog the marketplace caches,
/// both <c>null</c> when they are not said, on create and on every newer version; any other booking never gets them.</item>
/// <item><b>Rate limit.</b> The single reads wait for the bucket of the org to refill (at most <see cref="MaxRateWait"/>); a run that cannot go on
/// stops, is reported <see cref="ImportOutcome.Incomplete"/> and does not move the checkpoint of the listing.</item>
/// <item><b>Privacy.</b> The logs carry ids, counts and the type of an error, never a name, a note, an amount or a body.</item>
/// </list>
/// </remarks>
public sealed class BookingImportService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    OctorateRateLimiter rateLimiter,
    BookingStateMapper mapper,
    IDomainEventRecorder recorder,
    TimeProvider timeProvider,
    ILogger<BookingImportService> logger) : IBookingImportService
{
    /// <summary>Reservations asked for in a page.</summary>
    public const int PageSize = OctorateReservationQuery.MaxPageSize;

    /// <summary>Years of history the first full read goes back.</summary>
    public const int FullImportYears = 3;

    /// <summary>Calls of the bucket of the org that the import leaves to the people who use CasaZen.</summary>
    public const int RateReserve = 5;

    /// <summary>The longest the import waits for the bucket of an org to refill, in one run.</summary>
    public static readonly TimeSpan MaxRateWait = TimeSpan.FromMinutes(5);

    /// <summary>What the guests of an imported booking are kept for (GDPR art. 6.1.b: the stay contract), in <see cref="Guest.DataProcessingPurpose"/>.</summary>
    public const string GuestPurpose = "Channel manager booking (GDPR art. 6.1.b, contract)";

    private static readonly EventId AuditEvent = new(52_301, "OctorateBookingImportAudit");

    public async Task<ImportResult> ImportAllAsync(Guid propertyId, CancellationToken ct, BookingImportOptions? options = null)
    {
        var from = timeProvider.TodayInRomeAsDateOnly().AddYears(-FullImportYears);
        return await RunAsync(propertyId, from, null, options, ct);
    }

    public async Task<ImportResult> ImportIncrementalAsync(Guid propertyId, DateTime since, CancellationToken ct, BookingImportOptions? options = null)
    {
        var sinceUtc = since.Kind == DateTimeKind.Local ? since.ToUniversalTime() : DateTime.SpecifyKind(since, DateTimeKind.Utc);
        return await RunAsync(propertyId, RomeCalendar.DateInRome(sinceUtc), sinceUtc, options, ct);
    }

    public async Task SyncBookingStateAsync(Guid bookingId, BookingStatus newStatus, CancellationToken ct)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.Id, b.OrgId, b.PropertyId, b.OctorateReservationId, b.Source })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var change = mapper.ToOutbound(newStatus);
        if (change == OctorateStateChange.None || booking.OctorateReservationId is null)
        {
            logger.LogDebug("Booking {BookingId}: no Octorate state to send for {Status}", booking.Id, newStatus);
            return;
        }

        // A reservation that came from a portal is cancelled on the portal: a delete from here is not proven to be accepted, and a success
        // Octorate did not confirm is never reported.
        if (change == OctorateStateChange.Cancel && FiscalCopy.IsOtaBookingSource(booking.Source))
            throw new DomainRuleException(BookingErrorCodes.ChannelCancelNotAllowed, "OctorateChannelCancelNotAllowed");

        var mapping = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == booking.OrgId && m.PropertyId == booking.PropertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
        if (mapping is null)
        {
            logger.LogInformation("Booking {BookingId}: its property has no confirmed mapping, nothing sent to Octorate", booking.Id);
            return;
        }

        using (orgScope.Use(booking.OrgId))
        {
            switch (change)
            {
                case OctorateStateChange.CheckInEnd:
                    await client.SetCheckInStatusAsync(mapping, booking.OctorateReservationId, OctorateCheckInStatus.CheckInEnd, ct);
                    break;
                case OctorateStateChange.CheckOutEnd:
                    await client.SetCheckInStatusAsync(mapping, booking.OctorateReservationId, OctorateCheckInStatus.CheckOutEnd, ct);
                    break;
                default:
                    await client.CancelReservationAsync(mapping, booking.OctorateReservationId, ct);
                    break;
            }
        }

        logger.LogInformation("Booking {BookingId} of org {OrgId}: {Change} sent to Octorate", booking.Id, booking.OrgId, change);
    }

    private async Task<ImportResult> RunAsync(Guid propertyId, DateOnly from, DateTime? since, BookingImportOptions? options, CancellationToken ct)
    {
        var target = await ResolveTargetAsync(propertyId, ct);
        if (target is null)
        {
            logger.LogInformation("Octorate bookings of property {PropertyId}: no confirmed mapping, nothing read", propertyId);
            return ImportResult.Nothing(ImportOutcome.NotMapped);
        }

        if (target.ProductId is null)
        {
            logger.LogInformation(
                "Octorate bookings of property {PropertyId} (org {OrgId}): the room of the listing is not chosen, nothing read", propertyId, target.OrgId);
            return ImportResult.Nothing(ImportOutcome.RoomNotChosen);
        }

        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var to = RomeCalendar.DateInRome(startedAt).AddDays(1);
        var run = new RunState();
        var reachedEnd = false;

        using (orgScope.Use(target.OrgId))
        {
            for (var page = 0; page <= OctorateReservationQuery.MaxPage && !run.Stopped; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await WaitForCapacityAsync(target.OrgId, ct))
                {
                    run.Stopped = true;
                    break;
                }

                OctorateReservationPage listed;
                try
                {
                    var query = new OctorateReservationQuery(OctorateReservationListType.ModifiedBetween, from, to, page, PageSize);
                    listed = await client.GetReservationsAsync(target.AccommodationId, query, ct);
                }
                catch (OctorateRateLimitException)
                {
                    run.Stopped = true;
                    break;
                }

                // An item that cannot be read (no id, or one the column cannot hold) is not imported and not lost in silence: the run is not
                // complete, so the checkpoint stays and the next run reads it again.
                if (listed.Unreadable > 0)
                {
                    run.Failed += listed.Unreadable;
                    logger.LogWarning(
                        "Octorate reservation list of property {PropertyId} (org {OrgId}), page {Page}: {Items} items could not be read, the next run reads them again",
                        propertyId,
                        target.OrgId,
                        page,
                        listed.Unreadable);
                }

                foreach (var item in listed.Reservations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!await HandleAsync(target, item, options, run, ct))
                        break;
                }

                // The list ends with a page that has no item, not with a short one: a server may serve fewer than the size asked for.
                if (listed.ItemCount == 0)
                {
                    reachedEnd = true;
                    break;
                }
            }

            // Pages run out before the list does (or the run stopped): what was not read is not "nothing".
            if (!reachedEnd)
                run.Stopped = true;
        }

        var complete = !run.Stopped && run.Failed == 0;
        if (complete)
            await MarkReadAsync(target, startedAt, since, ct);

        logger.LogInformation(
            AuditEvent,
            "Octorate booking import of property {PropertyId} (org {OrgId}): {Created} created, {Updated} updated, {Skipped} skipped, {Unmapped} of other rooms, {Failed} failed, complete {Complete}",
            propertyId,
            target.OrgId,
            run.Created,
            run.Updated,
            run.Skipped,
            run.Unmapped.Count,
            run.Failed,
            complete);

        return new ImportResult(
            run.Created,
            run.Updated,
            run.Skipped,
            run.Unmapped,
            complete ? ImportOutcome.Completed : ImportOutcome.Incomplete,
            run.Failed);
    }

    /// <summary>Deals with one reservation of the list; <c>false</c> when the run has to stop.</summary>
    private async Task<bool> HandleAsync(Target target, OctorateReservation item, BookingImportOptions? options, RunState run, CancellationToken ct)
    {
        // A reservation of another room is not this property's.
        if (item.Product is not null && !target.Owns(item.Product))
        {
            run.Unmapped.Add(new UnmappedReservation(target.AccommodationId, item.Id, item.Product));
            return true;
        }

        var known = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == target.OrgId && b.OctorateReservationId == item.Id)
            .Select(b => new { b.OctorateUpdatedAt, b.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (known is not null && (IsNotNewer(item.UpdatedAt, known.OctorateUpdatedAt) || IsProtected(known.UpdatedAt, options)))
        {
            run.Skipped++;
            return true;
        }

        if (!await WaitForCapacityAsync(target.OrgId, ct))
        {
            run.Stopped = true;
            return false;
        }

        OctorateReservation detail;
        try
        {
            detail = await client.GetReservationAsync(target.AccommodationId, item.Id, ct);
        }
        catch (OctorateNotFoundException)
        {
            // Gone between the list and the read: nothing to import.
            run.Skipped++;
            return true;
        }
        catch (OctorateRateLimitException)
        {
            run.Stopped = true;
            return false;
        }
        catch (Exception ex) when (ex is OctorateServerException or OctorateRequestRejectedException)
        {
            run.Failed++;
            logger.LogWarning(
                "Octorate reservation {ReservationId} of property {PropertyId} could not be read ({Error}): the next run reads it again",
                item.Id,
                target.PropertyId,
                ex.GetType().Name);
            return true;
        }

        if (!target.Owns(detail.Product))
        {
            run.Unmapped.Add(new UnmappedReservation(target.AccommodationId, detail.Id, detail.Product));
            return true;
        }

        try
        {
            switch (await ApplyAsync(target, detail, options, ct))
            {
                case Applied.Created:
                    run.Created++;
                    break;
                case Applied.Updated:
                    run.Updated++;
                    break;
                default:
                    run.Skipped++;
                    break;
            }
        }
        catch (DbUpdateException ex)
        {
            // The type of the error only: the message of the database may quote the row.
            run.Failed++;
            logger.LogError(
                "Octorate reservation {ReservationId} of property {PropertyId} could not be saved ({Error}): the next run reads it again",
                detail.Id,
                target.PropertyId,
                ex.GetType().Name);
        }

        return true;
    }

    /// <summary>Applies one reservation under the advisory lock of its key and commits it with its events.</summary>
    private async Task<Applied> ApplyAsync(Target target, OctorateReservation reservation, BookingImportOptions? options, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OctorateReservation, $"{target.OrgId:N}:{reservation.Id}"));
        try
        {
            var applied = await ApplyUnderLockAsync(target, reservation, options, ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return applied;
        }
        finally
        {
            // Whatever happened, nothing of this reservation (not even an event that was not saved) is left for the next one.
            db.ChangeTracker.Clear();
        }
    }

    private async Task<Applied> ApplyUnderLockAsync(Target target, OctorateReservation reservation, BookingImportOptions? options, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(b => b.OrgId == target.OrgId && b.OctorateReservationId == reservation.Id, ct);

        // The version read may have been applied meanwhile, or be older than the one that was: nothing changes.
        if (booking is not null && (IsNotNewer(reservation.UpdatedAt, booking.OctorateUpdatedAt) || IsProtected(booking.UpdatedAt, options)))
            return Applied.Skipped;

        // The echo of a reservation CasaZen created itself, read before the push saved its id: the booking is there, it is linked, not made again.
        if (booking is null && await LinkOwnBookingAsync(target, reservation, ct))
            return Applied.Skipped;

        if (reservation.Status is not { } remote)
        {
            logger.LogWarning("Octorate reservation {ReservationId} of property {PropertyId} has a status CasaZen does not know: not imported", reservation.Id, target.PropertyId);
            return Applied.Skipped;
        }

        var checkIn = mapper.ToStayDate(reservation.CheckIn, target.Timezone);
        var checkOut = mapper.ToStayDate(reservation.CheckOut, target.Timezone);
        if (checkIn is not { } arrival || checkOut is not { } departure || departure <= arrival)
        {
            logger.LogWarning("Octorate reservation {ReservationId} of property {PropertyId} has no readable stay dates: not imported", reservation.Id, target.PropertyId);
            return Applied.Skipped;
        }

        var source = mapper.ToSource(reservation.ChannelId, reservation.ChannelName);
        var label = mapper.ToChannelLabel(source, reservation.ChannelName);
        var (guests, adults, children) = mapper.ToGuests(reservation);
        var prices = mapper.ToStayPrices(reservation, target.Currency);
        var amounts = mapper.ToOtaAmounts(reservation, target.Currency);
        var todayRome = timeProvider.TodayInRome();

        // Who collects (E08/F03/US03/T02, NF-77): a booking of a channel has it, any other (the host's own, CasaZen's, the agency's booking engine) never.
        var channelBooking = FiscalCopy.IsOtaBookingSource(source) && booking?.Source != BookingSource.Manual;
        var collection = channelBooking ? await CollectionOfAsync(reservation, ct) : OtaCollection.None;

        if (booking is null)
            return await CreateAsync(target, reservation, remote, (arrival, departure), (source, label), (guests, adults, children), prices, amounts, collection, todayRome, now, ct);

        var owned = await IsOwnedByCasaZenAsync(booking, ct);
        var status = mapper.ToBookingStatus(remote, booking.Status, owned);

        var statusChanged = status != booking.Status;

        // The booking is found by (org, Octorate id) only. When Octorate moves the reservation to a room that is mapped to another property of the
        // org, the booking follows its room: left on the old property it would hold the nights there and leave the new one looking free.
        var propertyChanged = booking.PropertyId != target.PropertyId;
        var datesChanged = booking.CheckInDate.Date != arrival.Date || booking.CheckOutDate.Date != departure.Date;
        var guestsChanged = booking.NumberOfGuests != guests || booking.NumberOfAdults != adults || booking.NumberOfChildren != children;

        // The amounts of a reservation that does not state the currency of the property are not imported, whatever else it says: the figures held
        // stay. A price Octorate does not give is not written either, so it is never replaced by a zero (ApplyMoney).
        var moneyChanged = !owned
                           && mapper.InCurrencyOf(reservation, target.Currency)
                           && (Differs(booking.BasePrice, prices.Lodging)
                               || Differs(booking.TotalPrice, prices.Total)
                               || Differs(booking.TouristTax, prices.TouristTax)
                               || booking.OtaCommission != amounts.Commission
                               || booking.OtaNetAmount != amounts.Net
                               || booking.OtaPaidAmount != amounts.Paid
                               || booking.OtaBalanceAmount != amounts.Balance);
        var originChanged = booking.Source != BookingSource.Manual && (booking.Source != source || booking.ChannelLabel != label);
        var note = Clip(reservation.Notes, 1000);
        var noteChanged = booking.SpecialRequests.Length == 0 && note.Length > 0;

        // The two flags of NF-77 are read as the figures are, but they are not money and not an event: a booking CasaZen owns keeps them as well, and
        // a change of them alone is saved without a modification of the booking being announced.
        var collectionChanged = channelBooking
                                && (booking.OtaCompanyCollect != collection.CompanyCollect
                                    || booking.OtaCommissionIncludedInPayments != collection.CommissionIncludedInPayments);

        // What the version applied is remembered even when nothing else changes, so the same version is not read twice.
        if (reservation.UpdatedAt is { } updatedAt)
            booking.OctorateUpdatedAt = updatedAt;

        if (!(statusChanged || propertyChanged || datesChanged || guestsChanged || moneyChanged || originChanged || noteChanged || collectionChanged))
        {
            await db.SaveChangesAsync(ct);
            return Applied.Skipped;
        }

        if (propertyChanged)
            booking.PropertyId = target.PropertyId;

        if (statusChanged)
        {
            booking.Status = status;
            if (status == BookingStatus.Confirmed)
            {
                booking.ConfirmedAt = now;
                booking.CancelledAt = null;
            }
            else if (status == BookingStatus.Cancelled)
            {
                booking.CancelledAt = reservation.CancelledAt ?? now;
            }
        }

        if (datesChanged)
        {
            booking.CheckInDate = arrival;
            booking.CheckOutDate = departure;
        }

        if (guestsChanged)
        {
            booking.NumberOfGuests = guests;
            booking.NumberOfAdults = adults;
            booking.NumberOfChildren = children;
        }

        if (moneyChanged)
            ApplyMoney(booking, prices, amounts);

        if (collectionChanged)
            ApplyCollection(booking, collection);

        if (originChanged)
        {
            booking.Source = source;
            booking.ChannelLabel = label;
        }

        if (noteChanged)
            booking.SpecialRequests = note;

        booking.UpdatedAt = now;
        await RecordChangeAsync(booking, statusChanged, propertyChanged || datesChanged || guestsChanged || moneyChanged, todayRome, ct);
        await db.SaveChangesAsync(ct);
        return Applied.Updated;
    }

    /// <summary>
    /// A reservation CasaZen created itself (E08/F01/US01): its <c>refer</c> is the code of the booking that was pushed, and the booking of the same
    /// org and property that holds that code and has no Octorate id yet is that booking, whose id the push has not saved yet (the reconciliation, a
    /// webhook or a lookup that found nothing conclusive came first). It is linked (id and version) and nothing else changes: a second booking on the
    /// same dates would make the push, which saves the same id later, hit the unique index of (org, Octorate id). <c>false</c> when there is no such
    /// booking; a booking of a channel is never taken for it.
    /// </summary>
    private async Task<bool> LinkOwnBookingAsync(Target target, OctorateReservation reservation, CancellationToken ct)
    {
        var reference = reservation.Refer?.Trim();
        if (string.IsNullOrEmpty(reference))
            return false;

        var own = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(
                b => b.OrgId == target.OrgId
                     && b.PropertyId == target.PropertyId
                     && b.BookingCode == reference
                     && b.OctorateReservationId == null,
                ct);
        if (own is null || FiscalCopy.IsOtaBookingSource(own.Source))
            return false;

        own.OctorateReservationId = reservation.Id;
        own.OctorateUpdatedAt = reservation.UpdatedAt ?? own.OctorateUpdatedAt;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Octorate reservation {ReservationId} of property {PropertyId}: the echo of booking {BookingId}, linked to it and not imported again",
            reservation.Id,
            target.PropertyId,
            own.Id);
        return true;
    }

    private async Task<Applied> CreateAsync(
        Target target,
        OctorateReservation reservation,
        OctorateBookingStatus remote,
        (DateTime CheckIn, DateTime CheckOut) stay,
        (BookingSource Source, string? Label) channel,
        (int Guests, int Adults, int Children) people,
        StayPrices prices,
        OtaAmounts amounts,
        OtaCollection collection,
        DateTime todayRome,
        DateTime now,
        CancellationToken ct)
    {
        var status = mapper.ToBookingStatus(remote, null);
        var guest = await MatchOrCreateGuestAsync(target.OrgId, reservation, now, ct);
        var booking = new Booking
        {
            OrgId = target.OrgId,
            PropertyId = target.PropertyId,
            GuestId = guest.Id,
            CheckInDate = stay.CheckIn,
            CheckOutDate = stay.CheckOut,
            NumberOfGuests = people.Guests,
            NumberOfAdults = people.Adults,
            NumberOfChildren = people.Children,
            Status = status,
            Source = channel.Source,
            ChannelLabel = channel.Label,
            SpecialRequests = Clip(reservation.Notes, 1000),
            OctorateReservationId = reservation.Id,
            OctorateUpdatedAt = reservation.UpdatedAt,
            ConfirmedAt = status == BookingStatus.Confirmed ? now : null,
            CancelledAt = status == BookingStatus.Cancelled ? reservation.CancelledAt ?? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyMoney(booking, prices, amounts);
        ApplyCollection(booking, collection);
        db.Bookings.Add(booking);

        // The booker is the first line of the Alloggiati communication; the other guests are asked for at the check-in.
        var bookerLine = StayGuest.FromBooker(booking, guest);
        bookerLine.Id = Guid.NewGuid();
        bookerLine.CreatedAt = now;
        bookerLine.UpdatedAt = now;
        db.StayGuests.Add(bookerLine);

        if (status != BookingStatus.Cancelled && booking.CheckOutDate.Date >= todayRome.Date)
            BookingEvents.RecordCreated(recorder, booking, BookingEventOrigin.Octorate);

        await db.SaveChangesAsync(ct);
        return Applied.Created;
    }

    /// <summary>
    /// The events of a change of an existing booking (ADR-033-R7): the confirmation and the cancellation when the state became that, the
    /// modification when the property, the dates, the guests or the amounts changed and the booking is not cancelled. None for a stay that is over.
    /// </summary>
    private async Task RecordChangeAsync(Booking booking, bool statusChanged, bool modified, DateTime todayRome, CancellationToken ct)
    {
        if (booking.CheckOutDate.Date < todayRome.Date)
            return;

        if (statusChanged && booking.Status == BookingStatus.Confirmed)
            await BookingEvents.RecordAsync(recorder, db, DomainEventType.BookingConfirmed, booking, BookingEventOrigin.Octorate, ct);

        if (statusChanged && booking.Status == BookingStatus.Cancelled)
            await BookingEvents.RecordAsync(recorder, db, DomainEventType.BookingCancelled, booking, BookingEventOrigin.Octorate, ct);

        if (modified && booking.Status != BookingStatus.Cancelled)
            await BookingEvents.RecordAsync(recorder, db, DomainEventType.BookingModified, booking, BookingEventOrigin.Octorate, ct);
    }

    /// <summary>
    /// Writes the figures Octorate gave. A price it did not give (or gave in another currency, or in none) is not written: the booking keeps the
    /// one it had. A booking that never had one is left with the default of the column, zero (the price columns are not nullable and this task adds
    /// no migration); the OTA figures beside it are <c>null</c>, "not available". The OTA figures are replaced as read: <c>null</c> is their way
    /// to say so.
    /// </summary>
    private static void ApplyMoney(Booking booking, StayPrices prices, OtaAmounts amounts)
    {
        if (prices.Lodging is { } lodging)
            booking.BasePrice = lodging;

        if (prices.Total is { } total)
            booking.TotalPrice = total;

        if (prices.TouristTax is { } tax)
        {
            booking.TouristTax = tax;
            booking.TouristTaxAmount = tax;
        }

        booking.OtaCommission = amounts.Commission;
        booking.OtaNetAmount = amounts.Net;
        booking.OtaPaidAmount = amounts.Paid;
        booking.OtaBalanceAmount = amounts.Balance;
    }

    /// <summary>
    /// Writes the two collection flags of a booking of a channel (E08/F03/US03/T02, NF-77), as read: <c>null</c> is their way to say that Octorate (or the
    /// catalog of the portals) does not say. Only the import writes them: no endpoint does, and the change of a booking (<c>PUT /api/bookings/{id}</c>)
    /// writes dates, guests and notes only (ADR-033-R3).
    /// </summary>
    private static void ApplyCollection(Booking booking, OtaCollection collection)
    {
        booking.OtaCompanyCollect = collection.CompanyCollect;
        booking.OtaCommissionIncludedInPayments = collection.CommissionIncludedInPayments;
    }

    /// <summary>
    /// The flags of a reservation of a channel: who collects comes from the reservation (<c>companyCollect</c>); whether the commission is already taken
    /// out of the payments is the portal's, read from the cache of the portal catalog the marketplace keeps (platform data, no org, never a call to
    /// Octorate while a booking is applied). A portal the catalog does not hold says nothing: <c>null</c>.
    /// </summary>
    private async Task<OtaCollection> CollectionOfAsync(OctorateReservation reservation, CancellationToken ct)
    {
        bool? commissionIncluded = null;
        if (reservation.ChannelId is { Length: > 0 } channelId)
        {
            commissionIncluded = await db.MarketplacePortals
                .AsNoTracking()
                .Where(p => p.PortalCode == channelId)
                .Select(p => (bool?)p.CommissionIncludedInPayments)
                .FirstOrDefaultAsync(ct);
        }

        return mapper.ToOtaCollection(reservation with { CommissionIncludedInPayments = commissionIncluded });
    }

    /// <summary>A figure Octorate gave and that differs from the one held; a figure it did not give changes nothing.</summary>
    private static bool Differs(decimal held, decimal? read) => read is { } value && held != value;

    /// <summary>
    /// A booking CasaZen owns: entered by the host, or one that has payments or a saved card in CasaZen. Octorate's figures do not replace
    /// its own, and a cancellation made in CasaZen is not undone by a read.
    /// </summary>
    private async Task<bool> IsOwnedByCasaZenAsync(Booking booking, CancellationToken ct)
    {
        if (booking.Source == BookingSource.Manual
            || booking.StripeCustomerId is not null
            || booking.StripePaymentMethodId is not null
            || booking.StripeSetupIntentId is not null)
        {
            return true;
        }

        return await db.Payments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AnyAsync(p => p.OrgId == booking.OrgId && p.BookingId == booking.Id, ct);
    }

    /// <summary>
    /// The guest of an imported booking (ADR-033-R6): the guest of the org with the same e-mail and the same name when the reservation carries
    /// an e-mail (Octorate gives contact data only with a special permission, so it normally does not), otherwise a new one. Never a match on
    /// the e-mail alone: two people can share an address. The lawful basis is the stay contract (art. 6.1.b), recorded as the purpose; no
    /// consent is claimed.
    /// </summary>
    private async Task<Guest> MatchOrCreateGuestAsync(Guid orgId, OctorateReservation reservation, DateTime now, CancellationToken ct)
    {
        var firstName = Clip(reservation.FirstName, 100);
        var lastName = Clip(reservation.LastName, 100);
        var email = reservation.GuestEmail?.Trim() ?? string.Empty;

        if (email.Length > 0)
        {
            var emailKey = email.ToLowerInvariant();
            var firstKey = firstName.ToLowerInvariant();
            var lastKey = lastName.ToLowerInvariant();
            var existing = await db.Guests
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(g => g.OrgId == orgId
                            && !g.IsDeleted
                            && !g.ErasureRequested
                            && g.DataAnonymizedDate == null
                            && g.Email.ToLower() == emailKey
                            && g.FirstName.ToLower() == firstKey
                            && g.LastName.ToLower() == lastKey)
                .OrderByDescending(g => g.CreatedAt)
                .ThenBy(g => g.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return existing;
        }

        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            PhoneNumber = reservation.GuestPhone ?? string.Empty,
            DataProcessingPurpose = GuestPurpose,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Guests.Add(guest);
        return guest;
    }

    /// <summary>Records on the mapping that everything up to <paramref name="startedAt"/> was read (see <see cref="OctorateListingMapping.BookingsImportedThrough"/>).</summary>
    private async Task MarkReadAsync(Target target, DateTime startedAt, DateTime? since, CancellationToken ct)
    {
        var mapping = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(m => m.Id == target.MappingId && m.OrgId == target.OrgId, ct);
        if (mapping is null)
            return;

        // A full read covers the whole history; a window covers up to now only when it reaches back to the last complete read.
        var covers = since is null || (mapping.BookingsImportedThrough is { } previous && since.Value <= previous);
        if (!covers)
        {
            db.ChangeTracker.Clear();
            return;
        }

        mapping.BookingsImportedThrough = startedAt;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<Target?> ResolveTargetAsync(Guid propertyId, CancellationToken ct)
    {
        var mapping = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new { m.Id, m.OrgId, m.OctorateAccommodationId, m.OctorateProductId })
            .FirstOrDefaultAsync(ct);
        if (mapping is null)
            return null;

        var property = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => p.Id == propertyId && p.OrgId == mapping.OrgId)
            .Select(p => new { p.Timezone, p.Currency })
            .FirstOrDefaultAsync(ct);
        return property is null
            ? null
            : new Target(
                mapping.Id,
                mapping.OrgId,
                propertyId,
                mapping.OctorateAccommodationId,
                mapping.OctorateProductId,
                property.Timezone,
                StayCurrency.Normalize(property.Currency));
    }

    /// <summary>
    /// Waits, at most <see cref="MaxRateWait"/>, until the bucket of the org has more than <see cref="RateReserve"/> calls; <c>false</c> when
    /// it did not refill in time.
    /// </summary>
    private async Task<bool> WaitForCapacityAsync(Guid orgId, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (rateLimiter.AvailableCalls(orgId) <= RateReserve)
        {
            if (waited >= MaxRateWait)
                return false;

            await Task.Delay(OctorateRateLimiter.BurstPeriod, timeProvider, ct);
            waited += OctorateRateLimiter.BurstPeriod;
        }

        return true;
    }

    /// <summary>The version read is not newer than the one applied (both known): it changes nothing.</summary>
    private static bool IsNotNewer(DateTime? read, DateTime? applied) => read is { } r && applied is { } a && r <= a;

    /// <summary>The booking was changed in CasaZen within the window the caller protects (the optimistic window of the reconciliation).</summary>
    private bool IsProtected(DateTime bookingUpdatedAt, BookingImportOptions? options) =>
        options?.SkipChangedWithin is { } window && bookingUpdatedAt > timeProvider.GetUtcNow().UtcDateTime - window;

    private static string Clip(string? text, int maxLength)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private enum Applied
    {
        Skipped,
        Created,
        Updated,
    }

    /// <summary>The property a run is about, read once: the org, the listing, the room and the clock and currency of the property.</summary>
    private sealed record Target(
        Guid MappingId,
        Guid OrgId,
        Guid PropertyId,
        string AccommodationId,
        string? ProductId,
        string Timezone,
        string Currency)
    {
        /// <summary>The reservation is on the room of the property; a reservation that does not say its room is not.</summary>
        public bool Owns(string? product) =>
            product is not null && ProductId is not null && string.Equals(product, ProductId, StringComparison.Ordinal);
    }

    private sealed class RunState
    {
        public int Created { get; set; }

        public int Updated { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public bool Stopped { get; set; }

        public List<UnmappedReservation> Unmapped { get; } = [];
    }
}
