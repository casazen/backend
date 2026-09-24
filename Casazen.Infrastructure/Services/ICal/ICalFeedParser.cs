using System.Text;
using Casazen.Core.Utilities;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// Reads the iCal feeds imported by CasaZen (OTA calendars of a property, supplier calendars) with Ical.Net and turns
/// them into calendar dates that do not depend on the server's time zone (PC-10, A2-10, A2-23, A9-13).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A document is valid when it starts with <c>BEGIN:VCALENDAR</c> and Ical.Net reads it. The number of events
/// does not matter: an empty calendar is valid and blocks nothing.</item>
/// <item><c>STATUS:CANCELLED</c> and <c>TRANSP:TRANSPARENT</c> events block nothing.</item>
/// <item>All-day values (<c>VALUE=DATE</c>) are calendar dates, read as written. Timed values are instants (UTC,
/// <c>TZID</c>, or floating = Europe/Rome wall clock, also for an unknown <c>TZID</c>), converted to Europe/Rome before
/// taking their date.</item>
/// <item>Recurring events (<c>RRULE</c>, <c>RDATE</c>, minus <c>EXDATE</c> and the instances replaced by a
/// <c>RECURRENCE-ID</c> override) are expanded between <c>recurrenceFrom</c> and <c>recurrenceUntil</c>. Rules that
/// repeat more than once a day are not expanded (they block no night and could produce millions of instances).</item>
/// <item>An event that cannot be read is skipped and counted (with its UID, so the caller can keep what it blocked
/// before); it never fails the other events.</item>
/// </list>
/// </remarks>
public static class ICalFeedParser
{
    private const string CalendarHeader = "BEGIN:VCALENDAR";

    /// <exception cref="ICalFormatException">The document is empty, is not an iCalendar or cannot be read.</exception>
    public static ICalFeedParseResult Parse(string? icsContent, DateOnly recurrenceFrom, DateOnly recurrenceUntil)
    {
        var text = WithoutControlCharacters((icsContent ?? string.Empty).TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
        if (text.Length == 0)
            throw new ICalFormatException(ICalFormatFailure.Empty);

        if (!text.StartsWith(CalendarHeader, StringComparison.OrdinalIgnoreCase))
            throw new ICalFormatException(ICalFormatFailure.NotICalendar);

        CalendarCollection calendars;
        try
        {
            calendars = CalendarCollection.Load(text);
        }
        catch (Exception ex)
        {
            throw new ICalFormatException(ICalFormatFailure.Unparsable, ex);
        }

        if (calendars.Count == 0)
            throw new ICalFormatException(ICalFormatFailure.NotICalendar);

        return new Reader(CollectWrittenUids(text), recurrenceFrom, recurrenceUntil)
            .Read(calendars.SelectMany(c => c.Events).ToList());
    }

    private sealed class Reader(HashSet<string> writtenUids, DateOnly recurrenceFrom, DateOnly recurrenceUntil)
    {
        private readonly List<ICalOccurrence> _occurrences = [];
        private readonly HashSet<string> _unreadableUids = new(StringComparer.Ordinal);
        private int _cancelled;
        private int _transparent;
        private int _unreadable;
        private int _unsupportedRecurrences;
        private string? _firstError;

        public ICalFeedParseResult Read(IReadOnlyList<CalendarEvent> events)
        {
            // Instances of a series replaced by an override (same UID + RECURRENCE-ID): the override is read on its own.
            var overridesByUid = events
                .Where(e => e.RecurrenceId is not null && UidOf(e) is not null)
                .GroupBy(e => UidOf(e)!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var calendarEvent in events)
            {
                try
                {
                    ReadEvent(calendarEvent, overridesByUid);
                }
                catch (Exception ex)
                {
                    Unreadable(ex.GetType().Name, UidOf(calendarEvent));
                }
            }

            return new ICalFeedParseResult
            {
                Occurrences = _occurrences,
                CancelledEvents = _cancelled,
                TransparentEvents = _transparent,
                UnreadableEvents = _unreadable,
                UnsupportedRecurrences = _unsupportedRecurrences,
                FirstUnreadableError = _firstError,
                UnreadableUids = _unreadableUids,
            };
        }

        private void ReadEvent(CalendarEvent calendarEvent, Dictionary<string, List<CalendarEvent>> overridesByUid)
        {
            if (IsCancelled(calendarEvent))
            {
                _cancelled++;
                return;
            }

            if (IsTransparent(calendarEvent))
            {
                _transparent++;
                return;
            }

            var uid = UidOf(calendarEvent);
            if (calendarEvent.DtStart is not { } start)
            {
                Unreadable("MissingDtStart", uid);
                return;
            }

            var summary = Clean(calendarEvent.Summary, keepSpaces: true);
            var isSeries = calendarEvent.RecurrenceId is null
                && (calendarEvent.RecurrenceRules.Count > 0 || calendarEvent.RecurrenceDates.Count > 0);

            if (!isSeries)
            {
                var end = calendarEvent.DtEnd
                    ?? (calendarEvent.Duration > TimeSpan.Zero ? start.Add(calendarEvent.Duration) : null);
                Add(uid, summary, start, end, inWindowOnly: false);
                return;
            }

            if (calendarEvent.RecurrenceRules.Any(IsMoreThanDaily))
            {
                _unsupportedRecurrences++;
                return;
            }

            var overrides = uid is not null && overridesByUid.TryGetValue(uid, out var list) ? list : [];
            var windowStart = new CalDateTime(recurrenceFrom.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), "UTC");
            var windowEnd = new CalDateTime(recurrenceUntil.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), "UTC");

            foreach (var occurrence in calendarEvent.GetOccurrences(windowStart, windowEnd))
            {
                var occurrenceStart = occurrence.Period.StartTime;
                if (overrides.Any(o => IsSameInstance(o.RecurrenceId, occurrenceStart)))
                    continue;

                var occurrenceEnd = occurrence.Period.EndTime;
                if ((occurrenceEnd is null || !occurrenceEnd.GreaterThan(occurrenceStart)) && occurrence.Period.Duration > TimeSpan.Zero)
                    occurrenceEnd = occurrenceStart.Add(occurrence.Period.Duration);

                Add(uid, summary, occurrenceStart, occurrenceEnd, inWindowOnly: true);
            }
        }

        private void Add(string? uid, string? summary, IDateTime start, IDateTime? end, bool inWindowOnly)
        {
            if (ToDates(start, end) is not { } dates)
            {
                Unreadable("EndBeforeStart", uid);
                return;
            }

            if (inWindowOnly && (dates.LastDay < recurrenceFrom || dates.StartDate >= recurrenceUntil))
                return;

            _occurrences.Add(new ICalOccurrence(uid, summary, dates.StartDate, dates.EndDate, dates.LastDay));
        }

        private void Unreadable(string reason, string? uid)
        {
            _unreadable++;
            _firstError ??= reason;
            if (uid is not null)
                _unreadableUids.Add(uid);
        }

        // Ical.Net invents a random UID for an event without one: only a UID written in the feed is stable.
        private string? UidOf(CalendarEvent calendarEvent)
        {
            var uid = calendarEvent.Uid?.Trim();
            return uid is not null && writtenUids.Contains(uid) ? Clean(uid, keepSpaces: false) : null;
        }
    }

    /// <summary>
    /// Calendar dates of an event: first day, day after the last night, last day touched. Null when it ends before it
    /// starts (or, all-day, on its start date).
    /// </summary>
    internal static (DateOnly StartDate, DateOnly EndDate, DateOnly LastDay)? ToDates(IDateTime start, IDateTime? end)
    {
        if (!start.HasTime)
        {
            // VALUE=DATE: the date as written. Ical.Net's AsUtc would shift it by the server's UTC offset (A2-23).
            var firstDay = DateOnly.FromDateTime(start.Value);
            var endDate = end is null ? firstDay.AddDays(1) : CalendarDateOf(end);
            return endDate > firstDay ? (firstDay, endDate, endDate.AddDays(-1)) : null;
        }

        var startUtc = ToUtc(start);
        var endUtc = end is null ? startUtc : ToUtc(end);
        if (endUtc < startUtc)
            return null;

        var startDay = RomeCalendar.DateInRome(startUtc);
        var lastDay = endUtc > startUtc ? RomeCalendar.DateInRome(endUtc.AddTicks(-1)) : startDay;
        return (startDay, RomeCalendar.DateInRome(endUtc), lastDay);
    }

    private static DateOnly CalendarDateOf(IDateTime value) =>
        value.HasTime ? RomeCalendar.DateInRome(ToUtc(value)) : DateOnly.FromDateTime(value.Value);

    /// <summary>
    /// The UTC instant of a timed value: as is when UTC, else the wall clock of its <c>TZID</c>. A floating time or an
    /// unknown <c>TZID</c> is read as Europe/Rome wall clock (where the properties are), never as the server's zone.
    /// </summary>
    internal static DateTime ToUtc(IDateTime value)
    {
        if (value.IsUtc)
            return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

        var wallClock = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        var zone = FindTimeZone(value.TzId) ?? RomeCalendar.TimeZone;
        if (zone.IsInvalidTime(wallClock))
            wallClock = wallClock.AddHours(1); // skipped by the spring-forward change: the same instant as one hour later

        return TimeZoneInfo.ConvertTimeToUtc(wallClock, zone);
    }

    private static TimeZoneInfo? FindTimeZone(string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId))
            return null;

        var id = tzId.Trim().Trim('"');
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;

        // Producers that prefix the Olson name, e.g. "/mozilla.org/20050126_1/Europe/Rome".
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 && TimeZoneInfo.TryFindSystemTimeZoneById($"{parts[^2]}/{parts[^1]}", out zone)
            ? zone
            : null;
    }

    private static bool IsSameInstance(IDateTime? recurrenceId, IDateTime occurrenceStart)
    {
        if (recurrenceId is null)
            return false;

        return recurrenceId.HasTime && occurrenceStart.HasTime
            ? ToUtc(recurrenceId) == ToUtc(occurrenceStart)
            : recurrenceId.Value.Date == occurrenceStart.Value.Date;
    }

    private static bool IsCancelled(CalendarEvent calendarEvent) =>
        string.Equals(calendarEvent.Status?.Trim(), EventStatus.Cancelled, StringComparison.OrdinalIgnoreCase);

    private static bool IsTransparent(CalendarEvent calendarEvent) =>
        string.Equals(calendarEvent.Transparency?.Trim(), TransparencyType.Transparent, StringComparison.OrdinalIgnoreCase);

    private static bool IsMoreThanDaily(RecurrencePattern rule) =>
        rule.Frequency is FrequencyType.None or FrequencyType.Secondly or FrequencyType.Minutely or FrequencyType.Hourly
        || rule.ByHour.Count > 1
        || rule.ByMinute.Count > 1
        || rule.BySecond.Count > 1;

    /// <summary>Text without control characters (a NUL byte is refused by PostgreSQL), trimmed; null when empty.</summary>
    private static string? Clean(string? value, bool keepSpaces)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c))
                builder.Append(c);
            else if (keepSpaces)
                builder.Append(' ');
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// The document without the control characters RFC 5545 forbids (all but tab, CR and LF): a stray NUL in one
    /// SUMMARY would otherwise make Ical.Net reject the whole feed, and PostgreSQL refuses it in text columns.
    /// </summary>
    private static string WithoutControlCharacters(string text)
    {
        if (!text.AsSpan().ContainsAny(ForbiddenControlCharacters))
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!ForbiddenControlCharacters.Contains(c))
                builder.Append(c);
        }

        return builder.ToString();
    }

    private static readonly System.Buffers.SearchValues<char> ForbiddenControlCharacters = System.Buffers.SearchValues.Create(
        Enumerable.Range(0, 0x20).Append(0x7F).Select(i => (char)i).Where(c => c is not ('\t' or '\r' or '\n')).ToArray());

    /// <summary>The UID values written in the document (unfolded, raw and unescaped).</summary>
    private static HashSet<string> CollectWrittenUids(string text)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        var unfolded = text
            .Replace("\r\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n\t", string.Empty, StringComparison.Ordinal)
            .Replace("\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\n\t", string.Empty, StringComparison.Ordinal);

        foreach (var rawLine in unfolded.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4 || !line.StartsWith("UID", StringComparison.OrdinalIgnoreCase) || (line[3] != ':' && line[3] != ';'))
                continue;

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
                continue;

            var value = line[(colon + 1)..].Trim();
            uids.Add(value);
            uids.Add(value
                .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("\\,", ",", StringComparison.Ordinal)
                .Replace("\\;", ";", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
                .Trim());
        }

        return uids;
    }
}
