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
/// <param name="IsRecurring">
/// True for an occurrence of a series (<c>RRULE</c>/<c>RDATE</c>) or an instance replaced by an override of one
/// (<c>RECURRENCE-ID</c>): the answer about a free-looking event is one per series, not one per occurrence.
/// </param>
/// <param name="Currency">ISO 4217 currency code from X-CURRENCY custom property; null when not present (ADR-002-R8, NF-02).</param>
public sealed record ICalOccurrence(
    string? Uid,
    string? Summary,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly LastDay,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay,
    bool IsRecurring = false,
    string? Currency = null)
{
    public bool HasNights => EndDate > StartDate;

    public bool HasDuration => EndUtc > StartUtc;
}

public sealed class ICalFeedParseResult
{
    public required IReadOnlyList<ICalOccurrence> Occurrences { get; init; }

    public int CancelledEvents { get; init; }

    public int TransparentEvents { get; init; }

    public IReadOnlyList<ICalOccurrence> TransparentOccurrences { get; init; } = [];

    public int UnreadableEvents { get; init; }

    public int UnsupportedRecurrences { get; init; }

    public string? FirstUnreadableError { get; init; }

    /// <summary>
    /// UIDs of the unreadable events: what they blocked before is kept, since an event the reader cannot understand
    /// is not proof that the reservation is gone.
    /// </summary>
    public IReadOnlySet<string> UnreadableUids { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public int SkippedEvents => UnreadableEvents + UnsupportedRecurrences;
}
