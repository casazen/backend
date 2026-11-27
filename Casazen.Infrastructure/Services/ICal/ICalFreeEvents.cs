using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// A "free-looking" event of the supplier's feed (<c>TRANSP:TRANSPARENT</c>) as one row to ask the supplier about: one per
/// series, one per single event.
/// </summary>
/// <param name="DecisionKey">Hex SHA-256 the answer is stored under (series: the UID; single event: the UID and its start).</param>
/// <param name="UidHash">Hex SHA-256 of the UID, or of the content of an event without one.</param>
/// <param name="Summary">Title cut to the length of a label: personal data, shown only to the supplier.</param>
/// <param name="Occurrences">Occurrences taken into account (from today on, within the cap).</param>
public sealed record FreeEventCandidate(
    string DecisionKey,
    string UidHash,
    string? Summary,
    bool IsAllDay,
    bool Recurring,
    DateTime NextStartUtc,
    DateTime NextEndUtc,
    int Occurrences);

/// <summary>What the supplier sync does with the transparent events of a feed (see <see cref="ICalFreeEvents.Plan"/>).</summary>
/// <param name="Candidates">The events to keep as rows: decided ones, and the undecided ones within the cap.</param>
/// <param name="SeenKeys">The decision key of every transparent event of the feed, whatever the cap: a decided event never loses its answer by falling out of the cap.</param>
/// <param name="Blocking">The occurrences that stay occupied: those of the events answered "Busy", and those not answered yet.</param>
/// <param name="OverCap">Occurrences of unanswered events beyond the cap: ignored (not occupied), as they were before this feature.</param>
public sealed record FreeEventPlan(
    IReadOnlyList<FreeEventCandidate> Candidates,
    IReadOnlySet<string> SeenKeys,
    IReadOnlyList<ICalOccurrence> Blocking,
    int OverCap);

public static class ICalFreeEvents
{
    public const int MaxPendingOccurrences = 200;

    public static FreeEventPlan Plan(
        IEnumerable<ICalOccurrence> transparent,
        IReadOnlyDictionary<string, SupplierFreeEventDecision> decisions,
        DateTime nowUtc,
        bool blockUndecided = true,
        int maxPendingOccurrences = MaxPendingOccurrences)
    {
        var groups = transparent
            .Select(o => (Key: KeyOf(o), Occurrence: o))
            .GroupBy(x => x.Key.DecisionKey, StringComparer.Ordinal)
            .ToList();

        var seen = groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var pendingTaken = groups
            .Where(g => !IsDecided(decisions, g.Key))
            .SelectMany(g => g.Select(x => (g.Key, x.Occurrence)))
            .OrderBy(x => x.Occurrence.StartUtc)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(maxPendingOccurrences)
            .ToList();
        var overCap = groups.Where(g => !IsDecided(decisions, g.Key)).Sum(g => g.Count()) - pendingTaken.Count;
        var pendingByKey = pendingTaken.ToLookup(x => x.Key, x => x.Occurrence, StringComparer.Ordinal);

        var candidates = new List<FreeEventCandidate>();
        var blocking = new List<ICalOccurrence>();
        foreach (var group in groups)
        {
            var decided = IsDecided(decisions, group.Key);
            var occurrences = decided
                ? group.Select(x => x.Occurrence).ToList()
                : pendingByKey[group.Key].ToList();
            if (occurrences.Count == 0)
                continue;

            occurrences.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            var next = occurrences.FirstOrDefault(o => o.EndUtc > nowUtc) ?? occurrences[0];
            var first = group.First();
            candidates.Add(new FreeEventCandidate(
                group.Key,
                first.Key.UidHash,
                ICalImportService.Truncate(occurrences.Select(o => o.Summary).FirstOrDefault(s => s is not null), SupplierAgendaLimits.LabelMaxLength),
                next.IsAllDay,
                occurrences.Any(o => o.IsRecurring),
                next.StartUtc,
                next.EndUtc,
                occurrences.Count));

            var isBusy = decisions.TryGetValue(group.Key, out var decision) && decision == SupplierFreeEventDecision.Busy;
            if (isBusy || (!decided && blockUndecided))
                blocking.AddRange(occurrences);
        }

        return new FreeEventPlan(candidates, seen, blocking, overCap);
    }

    private static bool IsDecided(IReadOnlyDictionary<string, SupplierFreeEventDecision> decisions, string key) =>
        decisions.TryGetValue(key, out var decision) && decision != SupplierFreeEventDecision.Pending;

    internal static (string DecisionKey, string UidHash) KeyOf(ICalOccurrence occurrence)
    {
        var identity = occurrence.Uid is not null
            ? "uid:" + occurrence.Uid
            : "content:" + ContentIdentity(occurrence);

        var scope = occurrence.IsRecurring
            ? "series"
            : occurrence.IsAllDay
                ? occurrence.StartDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                : occurrence.StartUtc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        return (Sha256Hex($"{identity}|{scope}"), Sha256Hex(identity));
    }

    // No UID: what stays the same at every sync. A series repeats at the same time of day (Rome) for the same length.
    private static string ContentIdentity(ICalOccurrence occurrence)
    {
        if (!occurrence.IsRecurring)
            return FormattableString.Invariant($"{occurrence.IsAllDay}|{occurrence.Summary}");

        var timeOfDay = occurrence.IsAllDay
            ? TimeSpan.Zero
            : TimeZoneInfo.ConvertTimeFromUtc(occurrence.StartUtc, RomeCalendar.TimeZone).TimeOfDay;
        var minutes = (long)(occurrence.EndUtc - occurrence.StartUtc).TotalMinutes;
        return FormattableString.Invariant($"{occurrence.IsAllDay}|{occurrence.Summary}|{timeOfDay}|{minutes}");
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
