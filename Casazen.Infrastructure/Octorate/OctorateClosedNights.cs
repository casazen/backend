using System.Globalization;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// From the days of an Octorate calendar to the blocks CasaZen keeps (E02/F02/US01/T03): which nights Octorate holds closed, which of them
/// nothing else in CasaZen takes already, and how the nights group into blocks. Pure: no clock and no database, so every rule is read in one place.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>A night.</b> A day of the calendar is the night that starts on it; a block from <c>FirstNight</c> to <c>CheckOut</c> takes the nights from
/// the first date (included) to the last (excluded), like every block and stay (<c>PropertyOccupancy</c>).</item>
/// <item><b>Closed.</b> <see cref="OctorateCalendarDay.IsClosed"/>: stopped, or no unit on a day that is on sale.</item>
/// <item><b>Not taken elsewhere.</b> The reservations of the channels arrive as bookings, the closures CasaZen makes are manual blocks and the
/// channels not yet handed over to Octorate still arrive as iCal blocks: a night any of them already takes is not closed a second time, so the
/// host sees one thing for one night, and the closure CasaZen sent to Octorate does not come back as a block of its own.</item>
/// </list>
/// </remarks>
internal static class OctorateClosedNights
{
    internal const string BlockSummary = "Octorate";

    internal const string UidPrefix = "octorate:";

    internal static string UidOf(DateOnly firstNight) =>
        string.Create(CultureInfo.InvariantCulture, $"{UidPrefix}{firstNight:yyyy-MM-dd}");

    internal static List<OctorateCalendarDay> DaysOf(IReadOnlyCollection<OctorateCalendarDay> days, string productId)
    {
        var own = days.Where(d => string.Equals(d.ProductId, productId, StringComparison.Ordinal)).ToList();
        if (own.Count > 0)
            return own;

        return days.Select(d => d.ProductId).Distinct(StringComparer.Ordinal).Count() == 1 ? [.. days] : [];
    }

    /// <summary>
    /// The answer reaches both ends of the window that was asked for: it holds a day on <paramref name="from"/> and one on <paramref name="to"/>
    /// (both included). An answer that stops short (days left out, a second page that was not followed) says nothing of the nights it leaves out,
    /// and a night that is not in the answer must not read as open: it would remove its block and hand the feeds over on a guess.
    /// </summary>
    internal static bool Covers(IReadOnlyCollection<OctorateCalendarDay> days, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(days);

        return days.Any(d => d.Date == from) && days.Any(d => d.Date == to);
    }

    internal static List<DateOnly> Of(
        IEnumerable<OctorateCalendarDay> days,
        DateOnly from,
        DateOnly to,
        IReadOnlySet<DateOnly> takenElsewhere)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(takenElsewhere);

        return [.. days
            .Where(d => d.IsClosed && d.Date >= from && d.Date <= to && !takenElsewhere.Contains(d.Date))
            .Select(d => d.Date)
            .Distinct()
            .Order()];
    }

    internal static List<NightRun> Runs(IReadOnlyList<DateOnly> nights)
    {
        ArgumentNullException.ThrowIfNull(nights);

        var runs = new List<NightRun>();
        var i = 0;
        while (i < nights.Count)
        {
            var first = nights[i];
            var last = first;
            while (i + 1 < nights.Count && nights[i + 1] == last.AddDays(1))
            {
                last = nights[i + 1];
                i++;
            }

            runs.Add(new NightRun(first, last.AddDays(1)));
            i++;
        }

        return runs;
    }

    internal static IEnumerable<DateOnly> NightsIn(DateTime start, DateTime end, DateOnly from, DateOnly to)
    {
        var night = DateOnly.FromDateTime(start);
        var last = DateOnly.FromDateTime(end);
        if (night < from)
            night = from;

        for (; night < last && night <= to; night = night.AddDays(1))
            yield return night;
    }
}

internal readonly record struct NightRun(DateOnly FirstNight, DateOnly CheckOut);
