namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// One busy occurrence of a feed event, as calendar dates in Europe/Rome and as the UTC instants it covers (never
/// instants of the server's zone).
/// </summary>
/// <param name="Uid">UID of the event as written in the feed, or null when the event has none.</param>
/// <param name="Summary">SUMMARY without control characters, or null.</param>
/// <param name="StartDate">First day: the DTSTART date of an all-day event, the Rome date of the start otherwise.</param>
/// <param name="EndDate">
/// Day after the last night (the check-out date): the DTEND date of an all-day event, the Rome date of the end
/// otherwise. Equal to <paramref name="StartDate"/> when the event covers no night (e.g. 10:00-12:00).
/// </param>
/// <param name="LastDay">Last calendar day the event touches (inclusive), for day-based calendars (suppliers).</param>
/// <param name="StartUtc">
/// First instant the event covers (SP-05). A timed event: DTSTART as an instant (<c>Z</c> as is, <c>TZID</c> by the wall clock
/// of that zone, a floating time or an unknown <c>TZID</c> by the wall clock of Europe/Rome). An all-day event: the start of
/// its first day in Europe/Rome.
/// </param>
/// <param name="EndUtc">
/// Instant the event ends, exclusive (SP-05): DTEND (or DTSTART plus DURATION) as an instant, equal to
/// <paramref name="StartUtc"/> for a timed event with no length. An all-day event: the start of the day after its last day
/// in Europe/Rome.
/// </param>
/// <param name="IsAllDay">
/// True for a <c>VALUE=DATE</c> event (SP-05): calendar days, no hours. The supplier sync closes these days; a timed event
/// with a length becomes a window of hours instead.
/// </param>
public sealed record ICalOccurrence(
    string? Uid,
    string? Summary,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly LastDay,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay)
{
    public bool HasNights => EndDate > StartDate;

    /// <summary>True when the event covers a stretch of time (<see cref="EndUtc"/> after <see cref="StartUtc"/>).</summary>
    public bool HasDuration => EndUtc > StartUtc;
}

/// <summary>
/// Outcome of reading a valid feed: the busy occurrences and how many events were left out and why, for the logs.
/// Zero occurrences is a valid result (an empty calendar frees every date it used to block).
/// </summary>
public sealed class ICalFeedParseResult
{
    public required IReadOnlyList<ICalOccurrence> Occurrences { get; init; }

    /// <summary>Events with <c>STATUS:CANCELLED</c>: they block nothing.</summary>
    public int CancelledEvents { get; init; }

    /// <summary>Events with <c>TRANSP:TRANSPARENT</c> (free time): they block nothing.</summary>
    public int TransparentEvents { get; init; }

    /// <summary>Events or occurrences without a usable start, or ending before they start.</summary>
    public int UnreadableEvents { get; init; }

    /// <summary>Recurring events repeating more than once a day (hourly, BYHOUR lists, ...): not expanded.</summary>
    public int UnsupportedRecurrences { get; init; }

    /// <summary>Exception type of the first unreadable event, for the logs (never the message: it can quote the feed).</summary>
    public string? FirstUnreadableError { get; init; }

    /// <summary>
    /// UIDs of the unreadable events: what they blocked before is kept, since an event the reader cannot understand
    /// is not proof that the reservation is gone.
    /// </summary>
    public IReadOnlySet<string> UnreadableUids { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public int SkippedEvents => UnreadableEvents + UnsupportedRecurrences;
}
