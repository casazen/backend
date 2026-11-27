namespace Casazen.Core.Services;

/// <summary>A range of days of a window, both ends included: the dates of one chunk.</summary>
/// <param name="From">First day, included.</param>
/// <param name="To">Last day, included.</param>
public sealed record CalendarChunkRange(DateOnly From, DateOnly To)
{
    /// <summary>How many days the range covers.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// Splits a window of dates into the ranges one call to the channel manager covers (E08/F05/US02/T01, NF-36, D7), and estimates what they cost in quota.
/// Pure: <c>Octorate:Calendar:MaxDaysPerCall</c> and <c>RoomDaysPerQuotaUnit</c> are arguments, never numbers of the code.
/// </summary>
public static class CalendarChunkPlanner
{
    /// <summary>
    /// The window from <paramref name="from"/> to <paramref name="toInclusive"/> (both included) as consecutive ranges of at most
    /// <paramref name="maxDaysPerCall"/> days, in order, without a gap or an overlap. Empty when the window is empty (<paramref name="toInclusive"/> before
    /// <paramref name="from"/>). A <paramref name="maxDaysPerCall"/> below 1 is read as 1.
    /// </summary>
    public static IReadOnlyList<CalendarChunkRange> Split(DateOnly from, DateOnly toInclusive, int maxDaysPerCall)
    {
        var chunks = new List<CalendarChunkRange>();
        var size = Math.Max(1, maxDaysPerCall);
        for (var start = from; start <= toInclusive; start = start.AddDays(size))
        {
            var end = start.AddDays(size - 1);
            chunks.Add(new CalendarChunkRange(start, end < toInclusive ? end : toInclusive));
        }

        return chunks;
    }

    /// <summary>
    /// The units of quota the channel manager counts for <paramref name="roomDays"/> room-days: one for every <paramref name="roomDaysPerUnit"/>, the last
    /// one started included, never less than one for a call that writes a day (decision D7: an estimate, the channel manager says what it counted).
    /// </summary>
    public static int EstimateQuotaUnits(int roomDays, int roomDaysPerUnit)
    {
        if (roomDays <= 0)
            return 0;

        var perUnit = Math.Max(1, roomDaysPerUnit);
        return (roomDays + perUnit - 1) / perUnit;
    }

    /// <summary>
    /// The units of quota of a whole window sent as calls of at most <paramref name="maxDaysPerCall"/> days: the sum of the estimate of each call. The
    /// number the push compares with the quota that is left, and the one a process records as <c>QuotaUnitsEstimated</c>.
    /// </summary>
    public static int EstimateQuotaUnits(DateOnly from, DateOnly toInclusive, int maxDaysPerCall, int roomDaysPerUnit) =>
        Split(from, toInclusive, maxDaysPerCall).Sum(chunk => EstimateQuotaUnits(chunk.Days, roomDaysPerUnit));
}
