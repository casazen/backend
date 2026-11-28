namespace Casazen.Core.Services;

public readonly record struct OwnershipPeriod(Guid OwnerId, decimal SharePercent, DateOnly Start, DateOnly? End)
{
    public bool Covers(DateOnly day) => Start <= day && (End is not { } end || end >= day);
}

public enum OwnershipIssueKind
{
    Gap = 1,

    Shortfall = 2,

    Excess = 3,

    OwnerOverlap = 4,
}

/// <summary>
/// A stretch of days where the shares of a property are wrong. <paramref name="To"/> is <c>null</c> only for an
/// <see cref="OwnershipIssueKind.OwnerOverlap"/> that never ends. <paramref name="TotalPercent"/> is the sum of the shares in force
/// (for an overlap, the sum of the two shares of the owner); <paramref name="OwnerId"/> is set only for an overlap.
/// </summary>
public sealed record OwnershipIssue(OwnershipIssueKind Kind, DateOnly From, DateOnly? To, decimal TotalPercent, Guid? OwnerId = null);

/// <summary>
/// The one rule of the shares of a property (ADR-012-R4, NF-109, AC-NF109-2): on every day of its timeline the shares in force add up
/// to exactly 100.00, and nobody holds the same property twice at once. The readiness of an owner and the owner import both ask it, so
/// the import can never write what the readiness would reject.
/// </summary>
/// <remarks>
/// <para><b>The timeline.</b> It starts at the later of <c>from</c> and the first start, and ends at the latest of today, the last
/// finite end and the last start: a period still in force is checked up to there, and a property whose owners all left before today has
/// a gap from the day after the last end up to today (nobody holds it now). A period that ends in the future with no successor is not a
/// gap: the days after it are not checked, unless a period is still open, which then holds the property alone: the day after the last planned
/// end is judged too (a co-owner who leaves and takes his share with him is a shortfall, not a hand-over).</para>
/// <para><b>Exact arithmetic.</b> The shares are <c>decimal(5,2)</c>, so the sums are exact: 33.33 + 33.33 + 33.33 is 99.99, a shortfall,
/// never rounded to 100.</para>
/// </remarks>
public static class OwnerOwnershipTimeline
{
    public const decimal FullShare = 100.00m;

    /// <summary>
    /// The stretches of days between <paramref name="from"/> and the end of the timeline where the shares of <paramref name="periods"/> do not
    /// add up to 100.00, and the overlaps of one owner with itself, ordered by day. Empty when the shares are right (and for no period at all:
    /// the caller decides what a property with no owner means).
    /// </summary>
    /// <param name="periods">Every period of one property, in Rome days.</param>
    /// <param name="from">The first day to check (the start of the first period when it is earlier).</param>
    /// <param name="today">Today in Rome: a period in force is checked up to it, and the days between the last end and it need an owner.</param>
    public static IReadOnlyList<OwnershipIssue> Analyze(IReadOnlyList<OwnershipPeriod> periods, DateOnly from, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(periods);
        if (periods.Count == 0)
            return [];

        var issues = new List<OwnershipIssue>();
        issues.AddRange(Overlaps(periods));

        var first = periods.Min(p => p.Start);
        var start = from > first ? from : first;
        var last = Latest(today, periods.Where(p => p.End is not null).Select(p => p.End!.Value).DefaultIfEmpty(DateOnly.MinValue).Max(), periods.Max(p => p.Start));

        // A period that ends on the last day of the timeline leaves the periods that are still open alone from the next day: that day is judged too,
        // so that a planned departure which leaves the others short (or over) is seen now and not when the day comes. Only a total of 0 after the
        // last planned end, nobody at all, is tolerated: no period is open then.
        if (periods.Any(p => p.End == last) && periods.Any(p => p.End is null))
            last = last.AddDays(1);

        if (start > last)
            return Ordered(issues);

        var boundaries = new SortedSet<DateOnly> { start };
        foreach (var period in periods)
        {
            if (period.Start > start && period.Start <= last)
                boundaries.Add(period.Start);

            if (period.End is { } end && end < DateOnly.MaxValue && end.AddDays(1) > start && end.AddDays(1) <= last)
                boundaries.Add(end.AddDays(1));
        }

        var days = boundaries.ToList();
        OwnershipIssue? open = null;
        for (var i = 0; i < days.Count; i++)
        {
            var segmentStart = days[i];
            var segmentEnd = i + 1 < days.Count ? days[i + 1].AddDays(-1) : last;
            var total = periods.Where(p => p.Covers(segmentStart)).Sum(p => p.SharePercent);
            var kind = KindOf(total);

            if (open is not null && kind == open.Kind && total == open.TotalPercent)
            {
                open = open with { To = segmentEnd };
                continue;
            }

            if (open is not null)
                issues.Add(open);

            open = kind is { } wrong ? new OwnershipIssue(wrong, segmentStart, segmentEnd, total) : null;
        }

        if (open is not null)
            issues.Add(open);

        return Ordered(issues);
    }

    public static bool IsComplete(IReadOnlyList<OwnershipPeriod> periods, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(periods);
        return periods.Where(p => p.Covers(day)).Sum(p => p.SharePercent) == FullShare;
    }

    private static OwnershipIssueKind? KindOf(decimal total) =>
        total == FullShare ? null : total == 0m ? OwnershipIssueKind.Gap : total < FullShare ? OwnershipIssueKind.Shortfall : OwnershipIssueKind.Excess;

    private static DateOnly Latest(DateOnly a, DateOnly b, DateOnly c) => a >= b && a >= c ? a : b >= c ? b : c;

    private static IEnumerable<OwnershipIssue> Overlaps(IReadOnlyList<OwnershipPeriod> periods)
    {
        foreach (var owner in periods.GroupBy(p => p.OwnerId))
        {
            var ordered = owner.OrderBy(p => p.Start).ThenBy(p => p.End ?? DateOnly.MaxValue).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                for (var j = 0; j < i; j++)
                {
                    var earlier = ordered[j];
                    var earlierEnd = earlier.End ?? DateOnly.MaxValue;
                    if (current.Start > earlierEnd)
                        continue;

                    var currentEnd = current.End ?? DateOnly.MaxValue;
                    var overlapEnd = currentEnd < earlierEnd ? currentEnd : earlierEnd;
                    yield return new OwnershipIssue(
                        OwnershipIssueKind.OwnerOverlap,
                        current.Start,
                        overlapEnd == DateOnly.MaxValue ? null : overlapEnd,
                        earlier.SharePercent + current.SharePercent,
                        owner.Key);
                }
            }
        }
    }

    private static IReadOnlyList<OwnershipIssue> Ordered(List<OwnershipIssue> issues) =>
        [.. issues.OrderBy(i => i.From).ThenBy(i => i.Kind).ThenBy(i => i.To ?? DateOnly.MaxValue)];
}
