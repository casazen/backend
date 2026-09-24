using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
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
/// Import of iCal feeds (#294, PC-10): <see cref="ICalFeedParser"/> with the recurrence window of
/// <see cref="ICalImportOptions"/> around today in Europe/Rome, and the mapping of the occurrences to property blocks
/// (<see cref="ToPropertyBlocks"/>) or supplier busy days (<see cref="ToBusyDays"/>).
/// </summary>
public class ICalImportService(TimeProvider timeProvider, IOptions<ICalImportOptions> options)
{
    /// <summary>Longest range of busy days taken from one supplier event.</summary>
    internal const int MaxBusyDaysPerEvent = 366;

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

    /// <summary>Supplier busy days: every calendar day an occurrence touches, from its first to its last day.</summary>
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

    private static ParsedCalendarBlock ToBlock(string key, ICalOccurrence occurrence) =>
        new(
            key,
            occurrence.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            occurrence.EndDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Truncate(occurrence.Summary, CalendarBlock.SummaryMaxLength));

    // Stable key of an event without UID: the same event gets the same key at every sync.
    private static string ContentKey(ICalOccurrence occurrence) =>
        Sha256Hex($"{occurrence.StartDate:yyyy-MM-dd}|{occurrence.EndDate:yyyy-MM-dd}|{occurrence.Summary}");

    private static string FitKey(string key) =>
        key.Length <= CalendarBlock.ExternalUidMaxLength ? key : HashedUidPrefix + Sha256Hex(key);

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
