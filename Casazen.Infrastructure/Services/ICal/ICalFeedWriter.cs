using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using CalendarModel = Ical.Net.Calendar;

namespace Casazen.Infrastructure.Services.ICal;

/// <summary>One event of an exported CasaZen feed.</summary>
public sealed record ICalFeedEvent(
    string? Uid,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Summary);

/// <summary>
/// Writes the public availability feed of a property (RFC 5545, Ical.Net per ADR-002). Moved unchanged out of the F0
/// spike (#289) by PC-10; the export format itself is PC-12.
/// </summary>
public static class ICalFeedWriter
{
    public static string Write(IEnumerable<ICalFeedEvent> events)
    {
        var calendar = new CalendarModel
        {
            ProductId = "-//CasaZen//Export//EN",
        };

        foreach (var feedEvent in events)
        {
            calendar.Events.Add(new CalendarEvent
            {
                Uid = feedEvent.Uid ?? Guid.NewGuid().ToString(),
                Summary = string.IsNullOrWhiteSpace(feedEvent.Summary) ? "Blocked" : feedEvent.Summary,
                Start = new CalDateTime(feedEvent.StartUtc),
                End = new CalDateTime(feedEvent.EndUtc),
            });
        }

        return new CalendarSerializer().SerializeToString(calendar)!;
    }
}
