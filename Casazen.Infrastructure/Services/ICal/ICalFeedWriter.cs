using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using CalendarModel = Ical.Net.Calendar;

namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// One event of an exported CasaZen feed: whole days from <paramref name="StartDate"/> (the first night) to
/// <paramref name="EndDate"/> (exclusive: the departure day, which stays free for a new arrival).
/// </summary>
/// <param name="Uid">Stable identifier of the stay or block: the same at every download, so the OTAs update the event
/// instead of adding a new one.</param>
/// <param name="Summary">Neutral text shown by the calendars ("Occupato"), never a name, email or note.</param>
public sealed record ICalFeedEvent(string Uid, DateOnly StartDate, DateOnly EndDate, string Summary);

/// <summary>
/// Writes the public availability feed of a property (RFC 5545, Ical.Net per ADR-002). Every event is all-day
/// (PC-12, A2-22): <c>DTSTART;VALUE=DATE</c> and <c>DTEND;VALUE=DATE</c>, with no time and no time zone, so an OTA never
/// moves a stay by a day while converting an instant to its own zone.
/// </summary>
public static class ICalFeedWriter
{
    public const string ProductId = "-//CasaZen//Export//EN";

    /// <exception cref="ArgumentException">An event without a night (end not after start), UID or summary.</exception>
    public static string Write(IEnumerable<ICalFeedEvent> events)
    {
        var calendar = new CalendarModel
        {
            ProductId = ProductId,
            Method = "PUBLISH",
        };

        foreach (var feedEvent in events)
        {
            if (feedEvent.EndDate <= feedEvent.StartDate)
                throw new ArgumentException($"Event {feedEvent.Uid} has no night: the end must be after the start.", nameof(events));
            if (string.IsNullOrWhiteSpace(feedEvent.Uid) || string.IsNullOrWhiteSpace(feedEvent.Summary))
                throw new ArgumentException("Every exported event needs a stable UID and a summary.", nameof(events));

            calendar.Events.Add(new CalendarEvent
            {
                Uid = feedEvent.Uid,
                Summary = feedEvent.Summary,
                Start = DateValue(feedEvent.StartDate),
                End = DateValue(feedEvent.EndDate),
            });
        }

        return new CalendarSerializer().SerializeToString(calendar)!;
    }

    // A calendar date: VALUE=DATE, no time, no TZID.
    private static CalDateTime DateValue(DateOnly date) =>
        new(date.Year, date.Month, date.Day) { HasTime = false };
}
