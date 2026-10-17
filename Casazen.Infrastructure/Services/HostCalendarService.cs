using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostCalendarService"/>
/// <remarks>
/// Reads in a fixed number of steps whatever the number of properties: the properties of the scope, then one query each for the
/// bookings (with their guest), the payments of those bookings (for how much of the total came in), the blocks and the
/// interventions; the alerts read the properties, the bookings and the blocks only. Which properties are read is the scope's
/// (<c>InScope</c>, in SQL); everything else is read by those property ids and the org, so nothing of another property or org
/// can enter.
/// </remarks>
public sealed class HostCalendarService(
    AppDbContext db,
    IConfiguration? configuration = null,
    TimeProvider? timeProvider = null) : IHostCalendarService
{
    private readonly IConfiguration _configuration = configuration ?? new ConfigurationBuilder().Build();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostCalendar> GetCalendarAsync(HostScope scope, HostCalendarQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        EnsureWindow(query.From, query.To);

        var requested = RequestedIds(query.PropertyIds);
        var (properties, total) = await ReadPropertiesAsync(scope, requested, HostCalendarLimits.MaxProperties, cancellationToken);
        var (nowUtc, todayRome) = Now();
        var today = DateOnly.FromDateTime(todayRome);
        if (properties.Count == 0)
            return new HostCalendar(today, total, [], []);

        var ids = properties.Select(p => p.Id).ToList();
        var cutoff = CheckoutHolds.CutoffAt(nowUtc, CheckoutHolds.GetTtlMinutes(_configuration));

        var bookings = await ReadBookingRowsAsync(scope, ids, query.From, query.To, cutoff, cancellationToken);
        var payments = await ReadPaymentsAsync(scope, bookings, cancellationToken);
        var blocks = await ReadBlockRowsAsync(scope, ids, query.From, query.To, cancellationToken);
        var services = await ReadServiceRowsAsync(scope, ids, query.From, query.To, cancellationToken);

        var items = new List<CalendarItem>(bookings.Count + blocks.Count + services.Count);
        items.AddRange(bookings.Select(row => BookingItem(row, payments[row.Id].ToList(), nowUtc)));
        items.AddRange(blocks.Where(row => !IsRepresentedByStay(row)).Select(row => BlockItem(row, todayRome)));
        items.AddRange(services.Select(ServiceItem).OfType<CalendarItem>());

        return new HostCalendar(
            today,
            total,
            properties,
            items.OrderBy(i => i.From).ThenBy(i => i.Type).ThenBy(i => i.Id).ToList());
    }

    public async Task<HostCalendarAlerts> GetAlertsAsync(HostScope scope, HostCalendarAlertsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        EnsureWindow(query.From, query.To);

        var requested = RequestedIds(query.PropertyIds);
        var (properties, _) = await ReadPropertiesAsync(scope, requested, null, cancellationToken);
        var (nowUtc, todayRome) = Now();
        var today = DateOnly.FromDateTime(todayRome);
        if (properties.Count == 0)
            return new HostCalendarAlerts(today, query.From, query.To, []);

        var ids = properties.Select(p => p.Id).ToList();
        var cutoff = CheckoutHolds.CutoffAt(nowUtc, CheckoutHolds.GetTtlMinutes(_configuration));
        var bookings = await ReadBookingRowsAsync(scope, ids, query.From, query.To, cutoff, cancellationToken);
        var blocks = await ReadBlockRowsAsync(scope, ids, query.From, query.To, cancellationToken);

        var alerts = CalendarAlertRules.Compute(new CalendarAlertInput(
            today,
            query.From,
            query.To,
            properties.Select(p => new CalendarAlertProperty(p.Id, p.Name)).ToList(),
            OccupantsOf(bookings, blocks)));
        return new HostCalendarAlerts(today, query.From, query.To, alerts);
    }

    // --- The range and the properties -----------------------------------------------------------------------

    private static void EnsureWindow(DateOnly from, DateOnly to)
    {
        if (to < from)
            throw new ArgumentException("The last day of the range is before the first.", nameof(to));

        if (to.DayNumber - from.DayNumber + 1 > HostCalendarLimits.MaxWindowDays)
        {
            throw new DomainRuleException(
                HostCalendarErrorCodes.WindowTooLarge, HostCalendarErrorCodes.WindowTooLargeMessageKey, HostCalendarLimits.MaxWindowDays);
        }
    }

    // The properties asked for by id, without repeats; null when none were asked for.
    private static List<Guid>? RequestedIds(IReadOnlyCollection<Guid>? ids)
    {
        if (ids is null || ids.Count == 0)
            return null;

        var distinct = ids.Distinct().ToList();
        if (distinct.Count > HostCalendarLimits.MaxProperties)
        {
            throw new DomainRuleException(
                HostCalendarErrorCodes.TooManyProperties, HostCalendarErrorCodes.TooManyPropertiesMessageKey, HostCalendarLimits.MaxProperties);
        }

        return distinct;
    }

    // The short-rent properties of the scope, by name. A property that is not the caller's is not read, so asking for it by id
    // gives nothing, the same as an id that does not exist. With a limit, one row more is read to know whether the list was cut.
    private async Task<(IReadOnlyList<CalendarProperty> Properties, int Total)> ReadPropertiesAsync(
        HostScope scope,
        List<Guid>? requested,
        int? limit,
        CancellationToken cancellationToken)
    {
        var query = db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.IsActive)
            .Where(PropertyRentalModeRules.IsShortRent)
            .InScope(scope);
        if (requested is { } wanted)
            query = query.Where(p => wanted.Contains(p.Id));

        var ordered = query.OrderBy(p => p.Name).ThenBy(p => p.Id);
        var rows = await (limit is { } take ? ordered.Take(take + 1) : ordered)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.City,
                p.RentalMode,
                p.IsPaused,
                p.MinNights,
                p.NightlyRate,
                p.WeekendSurchargePercent,
                p.CleaningFee,
            })
            .ToListAsync(cancellationToken);

        var total = rows.Count;
        if (limit is { } max && rows.Count > max)
        {
            rows.RemoveAt(rows.Count - 1);
            total = await query.CountAsync(cancellationToken);
        }

        var properties = rows
            .Select(p => new CalendarProperty(
                p.Id,
                p.Name,
                p.City,
                p.RentalMode,
                p.IsPaused ? CalendarPropertyStatus.Paused : CalendarPropertyStatus.Active,
                p.MinNights,
                PaymentCashRules.ToCents(p.NightlyRate),
                p.WeekendSurchargePercent,
                PaymentCashRules.ToCents(p.CleaningFee)))
            .ToList();
        return (properties, total);
    }

    // --- Bookings ----------------------------------------------------------------------------------------------

    private sealed record BookingRow(
        Guid Id,
        Guid PropertyId,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        BookingStatus Status,
        BookingSource Source,
        PaymentOption PaymentOption,
        Guid? ICalFeedId,
        string? ChannelLabel,
        OtaStayReviewReason? OtaReviewReason,
        DateTime? RequestExpiresAt,
        DateTime? GuestEmailVerifiedAt,
        int NumberOfGuests,
        decimal TotalPrice,
        string GuestFirstName,
        string GuestLastName);

    // The bookings that take their dates (CheckoutHolds.OccupiesDates: not cancelled, not an expired hold) and have a day in the
    // range (HostCalendarRange), of the properties given.
    private async Task<List<BookingRow>> ReadBookingRowsAsync(
        HostScope scope,
        List<Guid> propertyIds,
        DateOnly from,
        DateOnly to,
        HoldExpiryCutoff cutoff,
        CancellationToken cancellationToken) =>
        await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && propertyIds.Contains(b.PropertyId))
            .Where(HostCalendarRange.BookingShownIn(Utc(from), Utc(to)))
            .Where(CheckoutHolds.OccupiesDates(cutoff))
            .OrderBy(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .Select(b => new BookingRow(
                b.Id,
                b.PropertyId,
                b.CheckInDate,
                b.CheckOutDate,
                b.Status,
                b.Source,
                b.PaymentOption,
                b.ICalFeedId,
                b.ChannelLabel,
                b.OtaReviewReason,
                b.RequestExpiresAt,
                b.GuestEmailVerifiedAt,
                b.NumberOfGuests,
                b.TotalPrice,
                b.Guest.FirstName,
                b.Guest.LastName))
            .ToListAsync(cancellationToken);

    // What came in for each booking: one read for all of them, as the list of bookings does.
    private async Task<ILookup<Guid, BookingPaymentFact>> ReadPaymentsAsync(
        HostScope scope,
        IReadOnlyList<BookingRow> bookings,
        CancellationToken cancellationToken)
    {
        if (bookings.Count == 0)
            return Enumerable.Empty<BookingPaymentFact>().ToLookup(_ => Guid.Empty);

        var ids = bookings.Select(b => b.Id).ToList();
        var rows = await db.Payments
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && ids.Contains(p.BookingId))
            .Select(p => new { p.BookingId, p.Status, p.Amount, p.RefundedAmount })
            .ToListAsync(cancellationToken);
        return rows.ToLookup(p => p.BookingId, p => new BookingPaymentFact(p.Status, p.Amount, p.RefundedAmount));
    }

    private static BookingCalendarItem BookingItem(BookingRow row, IReadOnlyCollection<BookingPaymentFact> payments, DateTime nowUtc)
    {
        // The state of a "pay at the property" request is read from the booking itself (OnSiteRequests): a stand-in with only what it reads.
        var request = OnSiteRequests.StateOf(
            new Booking
            {
                Status = row.Status,
                Source = row.Source,
                PaymentOption = row.PaymentOption,
                RequestExpiresAt = row.RequestExpiresAt,
                GuestEmailVerifiedAt = row.GuestEmailVerifiedAt,
            },
            nowUtc);

        return new BookingCalendarItem(
            row.Id,
            row.PropertyId,
            DateOnly.FromDateTime(row.CheckInDate),
            DateOnly.FromDateTime(row.CheckOutDate),
            row.Status,
            $"{row.GuestFirstName} {row.GuestLastName}".Trim(),
            row.NumberOfGuests,
            BookingListRules.ChannelOf(row.Source),
            row.PaymentOption,
            request,
            request is null ? null : row.RequestExpiresAt,
            BookingListRules.PaymentStateOf(row.TotalPrice, payments),
            PaymentCashRules.ToCents(row.TotalPrice),
            row.ICalFeedId,
            row.ChannelLabel,
            // A cancelled stay has nothing left to check (CO-21).
            row.Status == BookingStatus.Cancelled ? null : row.OtaReviewReason);
    }

    // --- Blocks ------------------------------------------------------------------------------------------------

    private sealed record BlockRow(
        Guid Id,
        Guid PropertyId,
        CalendarBlockSource Source,
        DateTime StartUtc,
        DateTime EndUtc,
        string? Summary,
        CalendarBlockReason? ManualReason,
        Guid? FeedId,
        ICalFeedChannel? Channel,
        string? FeedLabel,
        Guid? BookingId,
        BookingStatus? StayStatus,
        DateTime? StayCheckIn,
        DateTime? StayCheckOut);

    // The blocks (iCal imports and the host's own) with a day in the range, of the properties given, with the channel and label of
    // their feed and where the stay made from them stands. A projection: the import URL of the feed is never read.
    private async Task<List<BlockRow>> ReadBlockRowsAsync(
        HostScope scope,
        List<Guid> propertyIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && propertyIds.Contains(b.PropertyId))
            .Where(HostCalendarRange.BlockShownIn(Utc(from), Utc(to)))
            .OrderBy(b => b.StartUtc)
            .ThenBy(b => b.Id)
            .Select(b => new BlockRow(
                b.Id,
                b.PropertyId,
                b.Source,
                b.StartUtc,
                b.EndUtc,
                b.Summary,
                b.ManualReason,
                b.FeedId,
                b.Feed != null ? (ICalFeedChannel?)b.Feed.Channel : null,
                b.Feed != null ? b.Feed.Label : null,
                b.BookingId,
                b.Booking != null ? (BookingStatus?)b.Booking.Status : null,
                b.Booking != null ? (DateTime?)b.Booking.CheckInDate : null,
                b.Booking != null ? (DateTime?)b.Booking.CheckOutDate : null))
            .ToListAsync(cancellationToken);

    // The block as the entity the occupancy and conversion rules read: only what they read.
    private static CalendarBlock BlockOf(BlockRow row) => new()
    {
        Id = row.Id,
        PropertyId = row.PropertyId,
        Source = row.Source,
        FeedId = row.FeedId,
        StartUtc = row.StartUtc,
        EndUtc = row.EndUtc,
        BookingId = row.BookingId,
    };

    private static Booking? StayOf(BlockRow row) =>
        row.BookingId is { } id && row.StayStatus is { } status && row.StayCheckIn is { } checkIn && row.StayCheckOut is { } checkOut
            ? new Booking { Id = id, Status = status, CheckInDate = checkIn, CheckOutDate = checkOut }
            : null;

    // A block the host turned into an OTA stay that stands for it counts once, as the stay (PropertyOccupancy, CO-21).
    private static bool IsRepresentedByStay(BlockRow row) => PropertyOccupancy.IsRepresentedByStay(BlockOf(row), StayOf(row));

    private static CalendarItem BlockItem(BlockRow row, DateTime todayRome)
    {
        var from = DateOnly.FromDateTime(row.StartUtc);
        var to = DateOnly.FromDateTime(row.EndUtc);

        if (row.Source == CalendarBlockSource.ICalImport)
        {
            return new IcalCalendarItem(
                row.Id,
                row.PropertyId,
                from,
                to,
                ChannelOf(row.Channel),
                row.FeedLabel,
                row.Channel is not null && OtaStays.IsConvertible(BlockOf(row), row.StayStatus, todayRome),
                row.StayStatus is not (null or BookingStatus.Cancelled) ? row.BookingId : null,
                row.Summary);
        }

        return new BlockCalendarItem(row.Id, row.PropertyId, from, to, row.ManualReason ?? CalendarBlockReason.Other, row.Summary);
    }

    private static BookingChannel ChannelOf(ICalFeedChannel? channel) => channel switch
    {
        ICalFeedChannel.Airbnb => BookingChannel.Airbnb,
        ICalFeedChannel.BookingCom => BookingChannel.BookingCom,
        _ => BookingChannel.Other,
    };

    // --- Interventions -----------------------------------------------------------------------------------------

    private sealed record ServiceRow(
        Guid Id,
        Guid PropertyId,
        string Category,
        string? ServiceName,
        ServiceRequestStatus Status,
        Guid SupplierOrgId,
        string SupplierName,
        Guid? BookingId,
        DateTime? ScheduledStartUtc,
        DateTime? ScheduledEndUtc,
        DateTime? StayCheckOut);

    // The short-rent interventions of the properties given that nobody refused or cancelled and that have a day in the range: the
    // day of their time (Europe/Rome) or, for a request with no time, the day its stay leaves (the day such a request has always had).
    private async Task<List<ServiceRow>> ReadServiceRowsAsync(
        HostScope scope,
        List<Guid> propertyIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var startUtc = RomeCalendar.StartOfDayUtc(from);
        var endUtc = RomeCalendar.StartOfDayUtc(to.AddDays(1));
        var firstDay = Utc(from);
        var dayAfter = Utc(to.AddDays(1));

        return await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.RentalContext == ServiceRequestRentalContext.ShortRent && r.PropertyId != null)
            .Where(r => propertyIds.Contains(r.PropertyId!.Value))
            .Where(r => r.Status != ServiceRequestStatus.Rifiutato && r.Status != ServiceRequestStatus.Annullato)
            .Where(r => (r.ScheduledStartUtc != null && r.ScheduledStartUtc >= startUtc && r.ScheduledStartUtc < endUtc)
                || (r.ScheduledStartUtc == null && r.Booking != null && r.Booking.CheckOutDate >= firstDay && r.Booking.CheckOutDate < dayAfter))
            .Select(r => new ServiceRow(
                r.Id,
                r.PropertyId!.Value,
                r.Category,
                r.ServiceNameSnapshot,
                r.Status,
                r.SupplierOrgId,
                r.SupplierOrg.DisplayName != "" ? r.SupplierOrg.DisplayName : r.SupplierOrg.Name,
                r.BookingId,
                r.ScheduledStartUtc,
                r.ScheduledEndUtc,
                r.Booking != null ? (DateTime?)r.Booking.CheckOutDate : null))
            .ToListAsync(cancellationToken);
    }

    // Null for a request that can be placed on no day (no time and no stay): it has no place on a calendar.
    private static ServiceCalendarItem? ServiceItem(ServiceRow row)
    {
        DateOnly? day = row.ScheduledStartUtc is { } start
            ? RomeCalendar.DateInRome(start)
            : row.StayCheckOut is { } checkOut ? DateOnly.FromDateTime(checkOut) : null;
        if (day is not { } placed)
            return null;

        return new ServiceCalendarItem(
            row.Id,
            row.PropertyId,
            placed,
            placed.AddDays(1),
            CalendarServiceKinds.Of(row.Category),
            row.Category,
            row.ServiceName,
            row.Status,
            row.SupplierOrgId,
            row.SupplierName,
            row.BookingId,
            row.ScheduledStartUtc,
            row.ScheduledEndUtc);
    }

    // --- Alerts ------------------------------------------------------------------------------------------------

    // What takes nights, for the alerts: the stays and the blocks (not the ones a stay stands for), as the calendar shows them.
    private static List<CalendarOccupant> OccupantsOf(IReadOnlyList<BookingRow> bookings, IReadOnlyList<BlockRow> blocks)
    {
        var occupants = new List<CalendarOccupant>(bookings.Count + blocks.Count);
        occupants.AddRange(bookings.Select(b => new CalendarOccupant(
            b.Id,
            CalendarItemType.Booking,
            b.PropertyId,
            DateOnly.FromDateTime(b.CheckInDate),
            DateOnly.FromDateTime(b.CheckOutDate))));
        occupants.AddRange(blocks.Where(b => !IsRepresentedByStay(b)).Select(b => new CalendarOccupant(
            b.Id,
            b.Source == CalendarBlockSource.ICalImport ? CalendarItemType.Ical : CalendarItemType.Block,
            b.PropertyId,
            DateOnly.FromDateTime(b.StartUtc),
            DateOnly.FromDateTime(b.EndUtc))));
        return occupants;
    }

    // --- Time --------------------------------------------------------------------------------------------------

    // Now, and today in Europe/Rome as midnight UTC of the date (the storage convention of stay dates).
    private (DateTime NowUtc, DateTime TodayRome) Now()
    {
        var now = _clock.GetUtcNow();
        return (now.UtcDateTime, RomeCalendar.TodayAt(now));
    }

    private static DateTime Utc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
