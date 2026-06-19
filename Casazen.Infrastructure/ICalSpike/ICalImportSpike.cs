using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using CalendarModel = Ical.Net.Calendar;

namespace Casazen.Infrastructure.ICalSpike;

/// <summary>
/// RFC 5545 import/export spike for F0 (#289). Uses Ical.Net per ADR-002.
/// </summary>
public static class ICalImportSpike
{
    public static IReadOnlyList<CalendarBlockSlice> ParseImport(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return [];

        var calendar = CalendarModel.Load(icsContent);
        var blocks = new List<CalendarBlockSlice>();

        foreach (var calendarEvent in calendar.Events)
        {
            if (calendarEvent.Start is null || calendarEvent.End is null)
                continue;

            var start = calendarEvent.Start.AsUtc;
            var end = calendarEvent.End.AsUtc;
            if (end <= start)
                continue;

            blocks.Add(new CalendarBlockSlice(
                calendarEvent.Uid,
                start,
                end,
                calendarEvent.Summary));
        }

        return blocks;
    }

    public static string BuildExportFeed(
        IEnumerable<CalendarBlockSlice> blocks,
        string calendarName = "CasaZen Availability")
    {
        var calendar = new CalendarModel
        {
            ProductId = "-//CasaZen//Export//EN",
        };

        foreach (var block in blocks)
        {
            var calendarEvent = new CalendarEvent
            {
                Uid = block.ExternalUid ?? Guid.NewGuid().ToString(),
                Summary = string.IsNullOrWhiteSpace(block.Summary) ? "Blocked" : block.Summary,
                Start = new CalDateTime(block.StartUtc),
                End = new CalDateTime(block.EndUtc),
            };
            calendar.Events.Add(calendarEvent);
        }

        return new CalendarSerializer().SerializeToString(calendar)!;
    }

    public static bool Overlaps(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IEnumerable<CalendarBlockSlice> blocks)
    {
        if (rangeEndUtc <= rangeStartUtc)
            return false;

        return blocks.Any(b => b.StartUtc < rangeEndUtc && b.EndUtc > rangeStartUtc);
    }

    public static bool IsValidExportFeed(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return false;

        if (!icsContent.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var parsed = CalendarModel.Load(icsContent);
            return parsed.Events.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
