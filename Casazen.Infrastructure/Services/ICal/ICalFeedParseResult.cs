namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// One busy occurrence of a feed event, as calendar dates in Europe/Rome (never instants of the server's zone).
/// </summary>
/// <param name="Uid">UID of the event as written in the feed, or null when the event has none.</param>
/// <param name="Summary">SUMMARY without control characters, or null.</param>
/// <param name="StartDate">First day: the DTSTART date of an all-day event, the Rome date of the start otherwise.</param>
/// <param name="EndDate">
/// Day after the last night (the check-out date): the DTEND date of an all-day event, the Rome date of the end
/// otherwise. Equal to <paramref name="StartDate"/> when the event covers no night (e.g. 10:00-12:00).
/// </param>
/// <param name="LastDay">Last calendar day the event touches (inclusive), for day-based calendars (suppliers).</param>
public sealed record ICalOccurrence(
    string? Uid,
    string? Summary,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly LastDay)
{
    public bool HasNights => EndDate > StartDate;
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
