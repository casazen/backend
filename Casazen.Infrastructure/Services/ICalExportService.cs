using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.ICalSpike;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// RFC 5545 export for property public iCal feed (#294). No PII in SUMMARY.
/// </summary>
public class ICalExportService
{
    public string BuildPropertyFeed(IEnumerable<Booking> bookings, IEnumerable<CalendarBlock> blocks)
    {
        var slices = new List<CalendarBlockSlice>();

        foreach (var booking in bookings.Where(b => b.Status != BookingStatus.Cancelled))
        {
            slices.Add(new CalendarBlockSlice(
                $"booking-{booking.Id}",
                booking.CheckInDate,
                booking.CheckOutDate,
                "Occupato"));
        }

        foreach (var block in blocks)
        {
            slices.Add(new CalendarBlockSlice(
                block.ExternalUid,
                block.StartUtc,
                block.EndUtc,
                string.IsNullOrWhiteSpace(block.Summary) ? "Occupato" : block.Summary));
        }

        return ICalImportSpike.BuildExportFeed(slices, "CasaZen Availability");
    }
}
