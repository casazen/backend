using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services.ICal;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Content of the public iCal export of a property (<c>GET /api/public/ical/{token}</c>, #294, PC-12, A2-22), read by
/// the OTAs to close the dates taken on CasaZen. See <c>docs/runbooks/ical.md</c> "Export feed".
/// <list type="bullet">
/// <item><b>Which bookings</b>: those that take their dates by the occupancy rule of the booking site (BK-05,
/// <see cref="CheckoutHolds.OccupiesDates"/>: not cancelled, not an expired checkout hold), minus a pending "pay at the
/// property" request (<see cref="OnSiteRequests.IsExportedToOtas"/>, BK-06) and minus the stays that came from an OTA
/// (<see cref="ExportsBooking"/>). The caller filters with those expressions.</item>
/// <item><b>Which blocks</b>: the manual blocks of the host only (<see cref="ExportsBlock"/>). Blocks imported from an
/// OTA feed never go back out: an OTA would read its own reservations back (echo: a reservation cancelled on Airbnb
/// stays closed on Airbnb), and the export has one URL per property, so it cannot tell which channel is reading it.
/// Each OTA learns about the other OTAs from their own feeds.</item>
/// <item><b>Format</b>: all-day events (<c>VALUE=DATE</c>, DTEND = departure day), UID stable per stay or block, SUMMARY
/// always the neutral text given by the caller: never a guest name, an email, a note or an imported SUMMARY.</item>
/// </list>
/// </summary>
public class ICalExportService
{
    // The OTA channels of a booking: the single list of FiscalCopy.IsOtaBookingSource, as a collection EF sends to SQL
    // (not an array: in an expression tree C# 14 would bind array.Contains to the span overload).
    private static readonly IReadOnlyList<BookingSource> OtaSources =
        Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToList();

    /// <summary>
    /// A booking that belongs to CasaZen (direct checkout, host booking, ...), not one that came from an OTA channel
    /// (Airbnb, Booking.com, ...): those are the OTA's own reservations, sending them back would be an echo.
    /// </summary>
    public static Expression<Func<Booking, bool>> ExportsBooking { get; } = b => !OtaSources.Contains(b.Source);

    /// <summary>A manual block of the host; never a block imported from an iCal feed (no echo).</summary>
    public static Expression<Func<CalendarBlock, bool>> ExportsBlock { get; } =
        b => b.Source == CalendarBlockSource.Manual && b.FeedId == null;

    // Declared after the expressions: static fields are initialized in the order they are written.
    private static readonly Func<Booking, bool> ExportsBookingCompiled = ExportsBooking.Compile();
    private static readonly Func<CalendarBlock, bool> ExportsBlockCompiled = ExportsBlock.Compile();

    /// <summary>
    /// The feed of <paramref name="bookings"/> (already filtered by occupancy, see the class remarks) and
    /// <paramref name="blocks"/>. Bookings from an OTA, cancelled bookings and imported blocks are left out here too.
    /// A stay or block that takes no night (end date not after start date, the rule of <see cref="PropertyOccupancy"/>)
    /// is left out. Events are ordered by date, then UID.
    /// </summary>
    /// <param name="busySummary">SUMMARY of every event, localized by the caller ("Occupato" / "Booked").</param>
    public string BuildPropertyFeed(IEnumerable<Booking> bookings, IEnumerable<CalendarBlock> blocks, string busySummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(busySummary);

        var events = new List<ICalFeedEvent>();

        foreach (var booking in bookings.Where(b => b.Status != BookingStatus.Cancelled).Where(ExportsBookingCompiled))
            AddEvent(events, BookingUid(booking.Id), booking.CheckInDate, booking.CheckOutDate, busySummary);

        foreach (var block in blocks.Where(ExportsBlockCompiled))
            AddEvent(events, BlockUid(block.Id), block.StartUtc, block.EndUtc, busySummary);

        return ICalFeedWriter.Write(events
            .OrderBy(e => e.StartDate)
            .ThenBy(e => e.Uid, StringComparer.Ordinal));
    }

    /// <summary>UID of a booking's event; unchanged since #294, so the events already read by the OTAs keep their identity.</summary>
    public static string BookingUid(Guid bookingId) => $"booking-{bookingId}";

    /// <summary>UID of a manual block's event: its id (before PC-12 a block without UID got a new random one at every download).</summary>
    public static string BlockUid(Guid blockId) => $"block-{blockId}";

    // Nights are calendar dates, as in PropertyOccupancy: [start date, end date).
    private static void AddEvent(List<ICalFeedEvent> events, string uid, DateTime start, DateTime end, string summary)
    {
        var startDate = DateOnly.FromDateTime(start.Date);
        var endDate = DateOnly.FromDateTime(end.Date);
        if (endDate > startDate)
            events.Add(new ICalFeedEvent(uid, startDate, endDate, summary));
    }
}
