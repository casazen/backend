using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// OTA stays created by the host from an iCal block (CO-21, decision D7). See <see cref="IOtaStayService"/> and the rules
/// in <see cref="OtaStays"/>. The stay is an ordinary confirmed booking: check-in link, Alloggiati Web, cockpit, arrival
/// and check-out use their own services, nothing is duplicated here. What a later sync does with the stay is in
/// <see cref="PropertyICalSyncService"/> (the stay is never changed there, only marked "da verificare").
/// </summary>
/// <remarks>
/// Locks, always in this order: the iCal feeds of the property (<see cref="PostgresAdvisoryLocks.Scope.PropertyICalSync"/>,
/// the sync writes blocks under it), the booking (<see cref="PostgresAdvisoryLocks.Scope.BookingCancellation"/>, when an
/// existing stay changes), then the dates of the property taken by <see cref="IBookingRepository"/> when it saves.
/// </remarks>
public sealed class OtaStayService(
    AppDbContext db,
    IBookingRepository bookingRepository,
    ICheckoutHoldExpiryService checkoutHoldExpiry,
    ILogger<OtaStayService> logger,
    TimeProvider? timeProvider = null) : IOtaStayService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CalendarBlock?> FindBlockAsync(Guid blockId, CancellationToken cancellationToken = default) =>
        await db.CalendarBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken);

    public async Task<Booking> ConvertBlockAsync(OtaStayConversion conversion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversion);

        var firstName = conversion.FirstName?.Trim() ?? string.Empty;
        var lastName = conversion.LastName?.Trim() ?? string.Empty;
        var email = conversion.Email?.Trim() ?? string.Empty;
        var guests = conversion.NumberOfGuests ?? OtaStays.DefaultNumberOfGuests;
        if (firstName.Length == 0 || lastName.Length == 0 || email.Length == 0 || guests < 1
            || conversion.TotalPrice is < 0)
            throw new DomainRuleException(BookingErrorCodes.CreateInvalid, "BookingCreateInvalid");

        var target = await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.Id == conversion.BlockId)
            .Select(b => new { b.PropertyId, b.StartUtc, b.EndUtc })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BlockNotFound(conversion.BlockId);

        // Abandoned checkout holds of the dates are released first, as for a booking entered by the host (BK-21). Outside
        // the locks: the expiry takes its own and may call Stripe.
        await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(
            target.PropertyId, target.StartUtc.Date, target.EndUtc.Date, cancellationToken);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyICalSync, target.PropertyId.ToString()));

        // Read again under the lock: a sync that held it has committed, the block may be gone or linked meanwhile.
        var block = await db.CalendarBlocks.FirstOrDefaultAsync(b => b.Id == conversion.BlockId, cancellationToken)
            ?? throw BlockNotFound(conversion.BlockId);
        if (block.Source != CalendarBlockSource.ICalImport || block.FeedId is not { } feedId)
            throw new DomainRuleException(OtaStayErrorCodes.BlockNotImported, OtaStayErrorCodes.BlockNotImportedMessageKey);

        if (block.BookingId is { } linkedId)
        {
            var linkedStatus = await db.Bookings
                .Where(b => b.Id == linkedId)
                .Select(b => (BookingStatus?)b.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (linkedStatus is not null and not BookingStatus.Cancelled)
            {
                throw new DomainConflictException(
                    OtaStayErrorCodes.AlreadyConverted, OtaStayErrorCodes.AlreadyConvertedMessageKey);
            }
        }

        var checkIn = block.StartUtc.Date;
        var checkOut = block.EndUtc.Date;
        if (checkOut < _clock.TodayInRome() || checkOut <= checkIn)
            throw new DomainRuleException(OtaStayErrorCodes.BlockEnded, OtaStayErrorCodes.BlockEndedMessageKey);

        // Projection: the feed's URL (encrypted) is never read here.
        var feed = await db.PropertyICalFeeds
            .AsNoTracking()
            .Where(f => f.Id == feedId)
            .Select(f => new { f.Channel, f.Label })
            .FirstOrDefaultAsync(cancellationToken);
        var source = feed is null ? null : OtaStays.SourceFor(feed.Channel, conversion.Source);
        if (source is null)
            throw new DomainRuleException(OtaStayErrorCodes.SourceRequired, OtaStayErrorCodes.SourceRequiredMessageKey);

        var maxGuests = await db.Properties
            .Where(p => p.Id == block.PropertyId)
            .Select(p => p.MaxGuests)
            .FirstAsync(cancellationToken);
        if (guests > maxGuests)
            throw new DomainRuleException(BookingErrorCodes.TooManyGuests, "BookingTooManyGuests", maxGuests);

        var now = _clock.GetUtcNow().UtcDateTime;
        // One guest per stay, owned by the property's org (TN-1): never a lookup by email.
        var guest = new Guest
        {
            OrgId = block.OrgId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var amount = decimal.Round(conversion.TotalPrice ?? 0m, 2, MidpointRounding.AwayFromZero);
        var stay = new Booking
        {
            PropertyId = block.PropertyId,
            OrgId = block.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = guests,
            NumberOfAdults = guests,
            NumberOfChildren = 0,
            Status = BookingStatus.Confirmed,
            Source = source.Value,
            ExternalId = block.ExternalUid ?? string.Empty,
            ICalFeedId = feedId,
            ChannelLabel = feed!.Label,
            // The channel's price is not in the feed: the host's amount when given, never one computed here.
            BasePrice = amount,
            TotalPrice = amount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Guests.Add(guest);
        block.BookingId = stay.Id;

        try
        {
            // Joins the transaction: the property's dates lock and the overlap check against every booking not cancelled.
            await bookingRepository.AddAsync(stay);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
        {
            db.ChangeTracker.Clear();
            throw new DomainConflictException(OtaStayErrorCodes.OverlapsBooking, OtaStayErrorCodes.OverlapsBookingMessageKey);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        // Ids only: the guest's name and email are personal data (FD-17).
        logger.LogInformation(
            "iCal block {BlockId} of property {PropertyId} converted into OTA stay {BookingId} ({Source}, feed {FeedId})",
            block.Id, block.PropertyId, stay.Id, stay.Source, feedId);
        return stay;
    }

    public async Task<Booking> ResolveReviewAsync(
        Guid bookingId,
        bool applyChannelDates = false,
        CancellationToken cancellationToken = default)
    {
        var current = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.PropertyId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BookingNotFound(bookingId);

        var channel = applyChannelDates ? await GetLinkedBlockAsync(bookingId, cancellationToken) : null;
        if (applyChannelDates && channel is null)
        {
            throw new DomainRuleException(
                OtaStayErrorCodes.ChannelDatesUnavailable, OtaStayErrorCodes.ChannelDatesUnavailableMessageKey);
        }

        if (channel is not null)
        {
            await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(
                current.PropertyId, channel.StartUtc.Date, channel.EndUtc.Date, cancellationToken);
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.PropertyICalSync, current.PropertyId.ToString()),
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")));

        var stay = await LoadAsync(bookingId, cancellationToken);
        var datesChanged = false;
        if (applyChannelDates)
        {
            // The block as committed now, under the feeds' lock.
            var block = await db.CalendarBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.BookingId == bookingId, cancellationToken)
                ?? throw new DomainRuleException(
                    OtaStayErrorCodes.ChannelDatesUnavailable, OtaStayErrorCodes.ChannelDatesUnavailableMessageKey);
            datesChanged = ApplyChannelDates(stay, block);
        }

        var wasFlagged = stay.OtaReviewReason is not null;
        stay.OtaReviewReason = null;
        stay.OtaReviewRaisedAt = null;
        if (datesChanged || wasFlagged)
            stay.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        if (datesChanged)
        {
            try
            {
                // Joins the transaction: the property's dates lock and the overlap check against the other bookings.
                await bookingRepository.UpdateAsync(stay);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
            {
                db.ChangeTracker.Clear();
                throw new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
            }
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        if (wasFlagged || datesChanged)
        {
            logger.LogInformation(
                "OTA stay {BookingId} verified by the host (channel dates applied: {DatesChanged})", bookingId, datesChanged);
        }

        return stay;
    }

    public async Task<CalendarBlock?> GetLinkedBlockAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        await db.CalendarBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.BookingId == bookingId, cancellationToken);

    public async Task<IReadOnlyList<OtaCalendarBlock>> GetCalendarBlocksAsync(
        Guid propertyId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.PropertyId == propertyId && b.StartUtc < endUtc && b.EndUtc > startUtc)
            .Select(b => new { Block = b, Stay = b.Booking })
            .ToListAsync(cancellationToken);

        // Channel and label of the feeds, by projection: the encrypted import URL is never read for the calendar.
        var feedIds = rows.Where(r => r.Block.FeedId != null).Select(r => r.Block.FeedId!.Value).Distinct().ToList();
        var feeds = feedIds.Count == 0
            ? new Dictionary<Guid, (ICalFeedChannel Channel, string? Label)>()
            : await db.PropertyICalFeeds
                .AsNoTracking()
                .Where(f => feedIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Channel, f.Label })
                .ToDictionaryAsync(f => f.Id, f => (f.Channel, f.Label), cancellationToken);

        var today = _clock.TodayInRome();
        return rows
            .Select(r =>
            {
                var feed = r.Block.FeedId is { } id && feeds.TryGetValue(id, out var found)
                    ? found
                    : ((ICalFeedChannel Channel, string? Label)?)null;
                var stayActive = r.Stay is not null && r.Stay.Status != BookingStatus.Cancelled;
                return new OtaCalendarBlock(
                    r.Block,
                    feed?.Channel,
                    feed?.Label,
                    stayActive ? r.Stay!.Id : null,
                    PropertyOccupancy.IsRepresentedByStay(r.Block, r.Stay),
                    OtaStays.IsConvertible(r.Block, r.Stay?.Status, today) && feed is not null);
            })
            .ToList();
    }

    // The stay takes the dates its block now has on the channel. Only a confirmed stay whose arrival is not registered
    // (the same limits as a change by the host, PC-07); the price stays the one the host gave.
    private bool ApplyChannelDates(Booking stay, CalendarBlock block)
    {
        var checkIn = block.StartUtc.Date;
        var checkOut = block.EndUtc.Date;
        if (checkIn == stay.CheckInDate.Date && checkOut == stay.CheckOutDate.Date)
            return false;

        if (stay.Status == BookingStatus.Cancelled)
            throw new DomainRuleException(BookingErrorCodes.NotEditable, "BookingNotEditable");
        if (stay.Status != BookingStatus.Confirmed)
            throw new DomainRuleException(BookingErrorCodes.StayLocked, "BookingStayLocked");
        if (checkIn != stay.CheckInDate.Date && checkIn < _clock.TodayInRome())
            throw new DomainRuleException(BookingErrorCodes.CheckInInPast, "BookingCheckInInPast");

        stay.CheckInDate = checkIn;
        stay.CheckOutDate = checkOut;
        return true;
    }

    /// <summary>
    /// The booking as committed now, under the lock. The context may already track it (the controller read it for the
    /// authorization): a query does not refresh a tracked entity, so it is reloaded.
    /// </summary>
    private async Task<Booking> LoadAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw BookingNotFound(bookingId);
        await db.Entry(booking).ReloadAsync(cancellationToken);
        return booking;
    }

    private static NotFoundException BlockNotFound(Guid blockId) =>
        new($"Calendar block {blockId} not found")
        {
            Code = OtaStayErrorCodes.BlockNotFound,
            MessageKey = OtaStayErrorCodes.BlockNotFoundMessageKey,
        };

    private static NotFoundException BookingNotFound(Guid bookingId) =>
        new($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
}
