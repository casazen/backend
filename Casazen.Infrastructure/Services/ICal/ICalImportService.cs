using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// A calendar block read from a property feed: the nights [<see cref="StartUtc"/>, <see cref="EndUtc"/>) as midnight
/// UTC of their Europe/Rome dates (the storage convention of date-only values), with a key unique within the feed.
/// </summary>
public sealed record ParsedCalendarBlock(
    string ExternalUid,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Summary);

/// <summary>The blocks of a property feed and how many duplicate entries were merged into them.</summary>
public sealed record PropertyICalBlocks(IReadOnlyList<ParsedCalendarBlock> Blocks, int MergedDuplicates);

/// <summary>
/// An engagement of a supplier's calendar feed that has hours (SP-05): the stretch [<see cref="StartUtc"/>,
/// <see cref="EndUtc"/>), with a key that is unique in the feed together with the start.
/// </summary>
/// <param name="ExternalUid">The UID of the event, or a stable hash for an event without one; at most 255 characters.</param>
/// <param name="StartUtc">First instant (UTC), a whole second.</param>
/// <param name="EndUtc">Instant it ends (UTC), after <paramref name="StartUtc"/>.</param>
/// <param name="Label">The SUMMARY cut to the length of a label, or null. Shown only in the supplier's own console.</param>
public sealed record ParsedSupplierWindow(string ExternalUid, DateTime StartUtc, DateTime EndUtc, string? Label);

/// <summary>
/// What a supplier's feed says about the supplier's time (SP-05): the <b>days</b> to close (all-day events, as before) and
/// the <b>windows of hours</b> to occupy (timed events), and how many occurrences were left out and why, for the logs.
/// </summary>
/// <param name="BusyDays">Days closed by the feed: every day an all-day event touches (and a timed event with no length).</param>
/// <param name="Windows">The hours the feed occupies, by start then key.</param>
/// <param name="MergedDuplicates">Occurrences with the same key and start folded into one (the longest is kept).</param>
/// <param name="OutsideWindow">Timed occurrences wholly before the import window or after it: not stored (yet).</param>
/// <param name="OverLimit">Windows left out because the supplier would have more than the limit (the farthest ones).</param>
public sealed record SupplierICalBusy(
    IReadOnlySet<DateOnly> BusyDays,
    IReadOnlyList<ParsedSupplierWindow> Windows,
    int MergedDuplicates,
    int OutsideWindow,
    int OverLimit);

/// <summary>
/// Import of iCal feeds (#294, PC-10): <see cref="ICalFeedParser"/> with the recurrence window of
/// <see cref="ICalImportOptions"/> around today in Europe/Rome, and the mapping of the occurrences to property blocks
/// (<see cref="ToPropertyBlocks"/>) or to what a supplier's feed occupies: busy days and windows of hours
/// (<see cref="ToSupplierBusy"/>, <see cref="ToBusyDays"/>).
/// </summary>
public class ICalImportService(TimeProvider timeProvider, IOptions<ICalImportOptions> options)
{
    /// <summary>Longest range of busy days taken from one supplier event.</summary>
    internal const int MaxBusyDaysPerEvent = 366;

    /// <summary>
    /// Most windows of hours one supplier's feed may occupy (SP-05). It bounds the rows and the work of every sync against a
    /// feed that expands to a great many occurrences (a dozen daily series over 18 months is already thousands); a normal
    /// calendar has a few hundred. The nearest windows are kept, so what the planner looks at is never the part dropped.
    /// </summary>
    internal const int MaxWindowsPerSupplier = 10_000;

    private const string HashedUidPrefix = "sha256:";

    // "#yyyyMMdd" appended to the UID of each occurrence of a UID with several ranges.
    private const int OccurrenceSuffixLength = 9;

    /// <exception cref="ICalFormatException">The document is not a readable iCalendar feed.</exception>
    public ICalFeedParseResult Parse(string? icsContent)
    {
        var today = timeProvider.TodayInRomeAsDateOnly();
        var window = options.Value;
        return ICalFeedParser.Parse(
            icsContent,
            today.AddMonths(-window.EffectiveMonthsBack),
            today.AddMonths(window.EffectiveMonthsAhead));
    }

    /// <summary>
    /// Property blocks: the occurrences that cover at least one night, each with a key that fits
    /// <see cref="CalendarBlock.ExternalUid"/> and is unique in the feed (A2-12).
    /// </summary>
    /// <remarks>
    /// The key is the UID, or a hash of dates and summary when the event has none. A UID that yields several ranges
    /// (a recurring series, or the same UID repeated by a broken feed) gets one key per start date
    /// (<c>UID#yyyyMMdd</c>) and none of its nights is dropped; identical repetitions are merged. A key longer than
    /// the column is replaced by its SHA-256. The summary is cut to <see cref="CalendarBlock.SummaryMaxLength"/>.
    /// </remarks>
    public static PropertyICalBlocks ToPropertyBlocks(IEnumerable<ICalOccurrence> occurrences)
    {
        var candidates = new List<(string Key, ICalOccurrence Occurrence)>();
        foreach (var group in occurrences.Where(o => o.HasNights).GroupBy(o => o.Uid ?? ContentKey(o), StringComparer.Ordinal))
        {
            var ranges = group
                .GroupBy(o => (o.StartDate, o.EndDate))
                .Select(g => g.First())
                .ToList();

            if (ranges.Count == 1)
            {
                candidates.Add((group.Key, ranges[0]));
                continue;
            }

            // Same UID and start date, different ends: keep the longest (every night stays blocked).
            candidates.AddRange(ranges
                .GroupBy(o => o.StartDate)
                .Select(sameStart => ($"{group.Key}#{sameStart.Key:yyyyMMdd}", sameStart.MaxBy(o => o.EndDate)!)));
        }

        var blocks = candidates
            .Select(c => (Key: FitKey(c.Key), c.Occurrence))
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Select(g => ToBlock(g.Key, g.First().Occurrence))
            .ToList();

        return new PropertyICalBlocks(blocks, occurrences.Count(o => o.HasNights) - blocks.Count);
    }

    /// <summary>
    /// True when <paramref name="externalUid"/> is the key <see cref="ToPropertyBlocks"/> gives to an event with one of
    /// <paramref name="uids"/>: the UID itself, an occurrence key <c>UID#yyyyMMdd</c> or the hash of a UID too long for
    /// the column. Used to keep the blocks of events the reader could not understand.
    /// </summary>
    public static bool IsKeyOfAny(string externalUid, IReadOnlySet<string> uids)
    {
        if (uids.Count == 0)
            return false;

        if (uids.Contains(externalUid))
            return true;

        var separator = externalUid.Length - OccurrenceSuffixLength;
        if (separator > 0
            && externalUid[separator] == '#'
            && !externalUid.AsSpan(separator + 1).ContainsAnyExceptInRange('0', '9')
            && uids.Contains(externalUid[..separator]))
        {
            return true;
        }

        return externalUid.StartsWith(HashedUidPrefix, StringComparison.Ordinal)
            && uids.Any(uid => string.Equals(FitKey(uid), externalUid, StringComparison.Ordinal));
    }

    /// <summary>Busy days: every calendar day an occurrence touches, from its first to its last day.</summary>
    /// <remarks>
    /// A pure mapping of the occurrences it is given. The supplier sync does <b>not</b> give it every occurrence any more
    /// (SP-05): <see cref="ToSupplierBusy"/> hands it the all-day ones, the timed ones become windows of hours.
    /// </remarks>
    public static IReadOnlySet<DateOnly> ToBusyDays(IEnumerable<ICalOccurrence> occurrences)
    {
        var days = new HashSet<DateOnly>();
        foreach (var occurrence in occurrences)
        {
            var day = occurrence.StartDate;
            for (var count = 0; day <= occurrence.LastDay && count < MaxBusyDaysPerEvent; count++)
            {
                days.Add(day);
                day = day.AddDays(1);
            }
        }

        return days;
    }

    /// <summary>
    /// What a supplier's feed occupies (SP-05): an <b>all-day</b> event closes its days, as it always did; a <b>timed</b> event
    /// with a length becomes a window of hours; a timed event with <b>no length</b> (DTEND equal to DTSTART, or none) has no hour
    /// to occupy and keeps closing its day, as before (the cautious reading: dropping it would offer a slot the supplier had
    /// closed).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The windows are those of the import window of <see cref="ICalImportOptions"/> (from <c>RecurrenceMonthsBack</c>
    /// months before today to <c>RecurrenceMonthsAhead</c> months after, Europe/Rome), the same one recurring events are expanded
    /// in; a single event further away is stored when it comes into it. The days are not limited, as before.</item>
    /// <item>The key of a window is the UID (an event without one gets a hash of its start, end and summary, the same at every
    /// sync); a key longer than the column is replaced by its SHA-256. A series gives one window per occurrence (same UID,
    /// other start). The same key and start twice (a broken feed, an override moved onto another instance) fold into the
    /// longest, so no hour is dropped and the unique index of the table is never hit.</item>
    /// <item>At most <see cref="MaxWindowsPerSupplier"/> windows, the nearest first; the rest is counted in
    /// <see cref="SupplierICalBusy.OverLimit"/>.</item>
    /// </list>
    /// </remarks>
    public SupplierICalBusy ToSupplierBusy(IEnumerable<ICalOccurrence> occurrences) =>
        ToSupplierBusy(occurrences, MaxWindowsPerSupplier);

    internal SupplierICalBusy ToSupplierBusy(IEnumerable<ICalOccurrence> occurrences, int maxWindows)
    {
        var today = timeProvider.TodayInRomeAsDateOnly();
        var window = options.Value;
        var windowStartUtc = RomeCalendar.StartOfDayUtc(today.AddMonths(-window.EffectiveMonthsBack));
        var windowEndUtc = RomeCalendar.StartOfDayUtc(today.AddMonths(window.EffectiveMonthsAhead));

        var dayBased = new List<ICalOccurrence>();
        var folded = new Dictionary<(string Key, DateTime StartUtc), ParsedSupplierWindow>();
        var timed = 0;
        var outsideWindow = 0;
        foreach (var occurrence in occurrences)
        {
            if (occurrence.IsAllDay || !occurrence.HasDuration)
            {
                dayBased.Add(occurrence);
                continue;
            }

            timed++;
            if (occurrence.EndUtc <= windowStartUtc || occurrence.StartUtc >= windowEndUtc)
            {
                outsideWindow++;
                continue;
            }

            var key = FitKey(occurrence.Uid ?? WindowContentKey(occurrence), SupplierAgendaLimits.ExternalUidMaxLength);
            var parsed = new ParsedSupplierWindow(
                key,
                occurrence.StartUtc,
                occurrence.EndUtc,
                Truncate(occurrence.Summary, SupplierAgendaLimits.LabelMaxLength));

            // Same key and start: the longest wins (every hour it covers stays occupied).
            if (!folded.TryGetValue((key, parsed.StartUtc), out var kept) || parsed.EndUtc > kept.EndUtc)
                folded[(key, parsed.StartUtc)] = parsed;
        }

        var ordered = folded.Values
            .OrderBy(w => w.StartUtc)
            .ThenBy(w => w.ExternalUid, StringComparer.Ordinal)
            .ToList();
        var overLimit = Math.Max(0, ordered.Count - maxWindows);
        if (overLimit > 0)
            ordered.RemoveRange(maxWindows, overLimit);

        return new SupplierICalBusy(
            BusyDays: ToBusyDays(dayBased),
            Windows: ordered,
            MergedDuplicates: timed - outsideWindow - folded.Count,
            OutsideWindow: outsideWindow,
            OverLimit: overLimit);
    }

    private static ParsedCalendarBlock ToBlock(string key, ICalOccurrence occurrence) =>
        new(
            key,
            occurrence.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            occurrence.EndDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Truncate(occurrence.Summary, CalendarBlock.SummaryMaxLength));

    // Stable key of an event without UID: the same event gets the same key at every sync.
    private static string ContentKey(ICalOccurrence occurrence) =>
        Sha256Hex($"{occurrence.StartDate:yyyy-MM-dd}|{occurrence.EndDate:yyyy-MM-dd}|{occurrence.Summary}");

    // The same for a window of hours: the instants, not the dates (two events of one day have two keys).
    private static string WindowContentKey(ICalOccurrence occurrence) =>
        Sha256Hex(FormattableString.Invariant(
            $"{occurrence.StartUtc:yyyyMMdd'T'HHmmss'Z'}|{occurrence.EndUtc:yyyyMMdd'T'HHmmss'Z'}|{occurrence.Summary}"));

    private static string FitKey(string key) => FitKey(key, CalendarBlock.ExternalUidMaxLength);

    private static string FitKey(string key, int maxLength) =>
        key.Length <= maxLength ? key : HashedUidPrefix + Sha256Hex(key);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>At most <paramref name="maxLength"/> characters, never splitting a surrogate pair.</summary>
    internal static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
            return value;

        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length].TrimEnd();
    }
}
