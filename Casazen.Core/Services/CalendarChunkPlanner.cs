namespace Casazen.Core.Services;

public sealed record CalendarChunkRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// Splits a window of dates into the ranges one call to the channel manager covers (E08/F05/US02/T01, NF-36, D7), and estimates what they cost in quota.
/// Pure: <c>Octorate:Calendar:MaxDaysPerCall</c> and <c>RoomDaysPerQuotaUnit</c> are arguments, never numbers of the code.
/// </summary>
public static class CalendarChunkPlanner
{
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

    public static int EstimateQuotaUnits(int roomDays, int roomDaysPerUnit)
    {
        if (roomDays <= 0)
            return 0;

        var perUnit = Math.Max(1, roomDaysPerUnit);
        return (roomDays + perUnit - 1) / perUnit;
    }

    public static int EstimateQuotaUnits(DateOnly from, DateOnly toInclusive, int maxDaysPerCall, int roomDaysPerUnit) =>
        Split(from, toInclusive, maxDaysPerCall).Sum(chunk => EstimateQuotaUnits(chunk.Days, roomDaysPerUnit));
}
