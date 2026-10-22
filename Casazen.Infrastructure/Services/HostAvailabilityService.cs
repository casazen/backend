using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostAvailabilityService"/>
/// <remarks>
/// Two reads, whatever the number of bookings: the bookings that take their dates (<see cref="CheckoutHolds.OccupiesDates"/>, with
/// the same hold TTL as the calendar and the public availability) and the calendar blocks (<see cref="PropertyOccupancy"/>), from
/// <see cref="HostAvailabilityRules.NearestWindowDays"/> days before the arrival to as many after the departure, so that the stay
/// asked for, its conflicts and the nearest free window come out of the same rows. They are read through the tenant filter and by the
/// id of the property the caller was authorized on; the encrypted import URL of a feed is never read. The only difference with the
/// final check that refuses a booking on taken dates is timing: an expired checkout hold is shown free here and released (or kept, if
/// its guest paid meanwhile) by that check (BK-21).
/// </remarks>
public sealed class HostAvailabilityService(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IHostAvailabilityService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostStayAvailability> CheckAsync(
        Property property,
        DateOnly from,
        DateOnly to,
        BookingChannel? channel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        // PM-01: a property in long-term mode takes no stay, the same answer as the quote of the host.
        PropertyRentalModeRules.EnsureShortRent(property);

        var today = _clock.TodayInRomeAsDateOnly();
        var nights = to.DayNumber - from.DayNumber;
        var minNights = property.MinNights;
        if (nights < 1)
            return Invalid(HostAvailabilityInvalidReason.CheckOutNotAfterCheckIn, 0, minNights);
        if (from < today)
            return Invalid(HostAvailabilityInvalidReason.CheckInInPast, nights, minNights);
        if (nights > TouristTaxCalculator.MaxNights)
            return Invalid(HostAvailabilityInvalidReason.StayTooLong, nights, minNights);

        var readFrom = Utc(from.AddDays(-HostAvailabilityRules.NearestWindowDays));
        var readTo = Utc(to.AddDays(HostAvailabilityRules.NearestWindowDays));
        var cutoff = CheckoutHolds.CutoffAt(_clock.GetUtcNow().UtcDateTime, CheckoutHolds.GetTtlMinutes(configuration));

        var bookings = await db.Bookings
            .AsNoTracking()
            .Where(PropertyOccupancy.BookingTakesNightIn(property.Id, readFrom, readTo))
            .Where(CheckoutHolds.OccupiesDates(cutoff))
            .Select(b => new BookingRow(b.Id, b.CheckInDate, b.CheckOutDate, b.Source, b.Guest.FirstName, b.Guest.LastName))
            .ToListAsync(cancellationToken);

        var blocks = await db.CalendarBlocks
            .AsNoTracking()
            .Where(PropertyOccupancy.BlockTakesNightIn(property.Id, readFrom, readTo))
            .Select(b => new BlockRow(
                b.Id,
                b.StartUtc,
                b.EndUtc,
                b.Source,
                b.FeedId,
                b.Summary,
                b.Feed != null ? (ICalFeedChannel?)b.Feed.Channel : null,
                b.Feed != null ? b.Feed.Label : null,
                b.BookingId,
                b.Booking != null ? (BookingStatus?)b.Booking.Status : null))
            .ToListAsync(cancellationToken);

        bool Overlaps(DateOnly start, DateOnly end) => start < to && from < end;

        var conflicts = new List<HostAvailabilityConflict>();
        foreach (var booking in bookings.Where(b => Overlaps(Day(b.CheckIn), Day(b.CheckOut))))
        {
            var name = $"{booking.GuestFirstName} {booking.GuestLastName}".Trim();
            conflicts.Add(new HostAvailabilityConflict(
                booking.Id,
                HostAvailabilityConflictType.Booking,
                name.Length == 0 ? null : name,
                BookingListRules.ChannelOf(booking.Source),
                Day(booking.CheckIn),
                Day(booking.CheckOut)));
        }

        var linkable = new List<BlockRow>();
        foreach (var block in blocks.Where(b => Overlaps(Day(b.Start), Day(b.End))))
        {
            var portal = block.Source == CalendarBlockSource.ICalImport && block.FeedChannel is not null;
            conflicts.Add(new HostAvailabilityConflict(
                block.Id,
                portal ? HostAvailabilityConflictType.Ical : HostAvailabilityConflictType.Block,
                portal ? block.FeedLabel : block.Summary,
                portal ? ChannelOf(block.FeedChannel) : null,
                Day(block.Start),
                Day(block.End)));

            if (portal && IsLinkable(block, from, to, today, channel))
                linkable.Add(block);
        }

        conflicts = conflicts.OrderBy(c => c.From).ThenBy(c => c.Type).ThenBy(c => c.Id).ToList();

        if (conflicts.Count == 0)
        {
            var below = minNights is { } minimum && nights < minimum;
            return new HostStayAvailability(
                below ? HostAvailabilityState.BelowMinimum : HostAvailabilityState.Ok, null, nights, minNights, [], null, null);
        }

        // A reservation of a portal that sits on exactly these dates and nothing else: the host completes it, not a second booking.
        if (conflicts.Count == 1 && linkable.Count == 1 && linkable[0].Id == conflicts[0].Id)
            return new HostStayAvailability(HostAvailabilityState.LinkableBlock, null, nights, minNights, conflicts, null, linkable[0].Id);

        var taken = bookings.Select(b => (Day(b.CheckIn), Day(b.CheckOut)))
            .Concat(blocks.Select(b => (Day(b.Start), Day(b.End))))
            .ToList();
        var window = HostAvailabilityRules.NearestFreeWindow(taken, from, nights, today);
        return new HostStayAvailability(HostAvailabilityState.Occupied, null, nights, minNights, conflicts, window, null);
    }

    private static HostStayAvailability Invalid(HostAvailabilityInvalidReason reason, int nights, int? minNights) =>
        new(HostAvailabilityState.Invalid, reason, nights, minNights, [], null, null);

    // The block of a portal's calendar that the host can turn into the stay: exactly these dates, not yet a stay (OtaStays.IsConvertible),
    // and, when the host says which portal, the calendar of that portal.
    private static bool IsLinkable(BlockRow block, DateOnly from, DateOnly to, DateOnly today, BookingChannel? channel)
    {
        if (Day(block.Start) != from || Day(block.End) != to)
            return false;

        if (channel is { } wanted && ChannelOf(block.FeedChannel) != wanted)
            return false;

        var stand = new CalendarBlock
        {
            Source = block.Source,
            FeedId = block.FeedId,
            BookingId = block.BookingId,
            StartUtc = block.Start,
            EndUtc = block.End,
        };
        return OtaStays.IsConvertible(stand, block.StayStatus, Utc(today));
    }

    private static BookingChannel ChannelOf(ICalFeedChannel? channel) => channel switch
    {
        ICalFeedChannel.Airbnb => BookingChannel.Airbnb,
        ICalFeedChannel.BookingCom => BookingChannel.BookingCom,
        _ => BookingChannel.Other,
    };

    // Stay dates are calendar dates, kept as midnight UTC: the date of the stored value, as PropertyOccupancy reads them in SQL.
    private static DateOnly Day(DateTime value) => DateOnly.FromDateTime(value);

    private static DateTime Utc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private sealed record BookingRow(
        Guid Id,
        DateTime CheckIn,
        DateTime CheckOut,
        BookingSource Source,
        string GuestFirstName,
        string GuestLastName);

    private sealed record BlockRow(
        Guid Id,
        DateTime Start,
        DateTime End,
        CalendarBlockSource Source,
        Guid? FeedId,
        string? Summary,
        ICalFeedChannel? FeedChannel,
        string? FeedLabel,
        Guid? BookingId,
        BookingStatus? StayStatus);
}
