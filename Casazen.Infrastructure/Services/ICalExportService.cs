using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services.ICal;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// RFC 5545 export for property public iCal feed (#294). No PII in SUMMARY.
/// </summary>
public class ICalExportService
{
    public string BuildPropertyFeed(IEnumerable<Booking> bookings, IEnumerable<CalendarBlock> blocks)
    {
        var events = new List<ICalFeedEvent>();

        foreach (var booking in bookings.Where(b => b.Status != BookingStatus.Cancelled))
        {
            events.Add(new ICalFeedEvent(
                $"booking-{booking.Id}",
                booking.CheckInDate,
                booking.CheckOutDate,
                "Occupato"));
        }

        foreach (var block in blocks)
        {
            events.Add(new ICalFeedEvent(
                block.ExternalUid,
                block.StartUtc,
                block.EndUtc,
                string.IsNullOrWhiteSpace(block.Summary) ? "Occupato" : block.Summary));
        }

        return ICalFeedWriter.Write(events);
    }
}
