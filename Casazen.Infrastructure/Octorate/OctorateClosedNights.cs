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
    /// <summary>The note on a block: the name of the service, not a text of a language. Shown to the host only, never exported.</summary>
    internal const string BlockSummary = "Octorate";

    /// <summary>Prefix of the UID of a block: the key <c>(PropertyId, ExternalUid)</c> of the blocks read from the Octorate calendar.</summary>
    internal const string UidPrefix = "octorate:";

    /// <summary>The UID of the block that starts with <paramref name="firstNight"/>: the same closure keeps its row from one run to the next.</summary>
    internal static string UidOf(DateOnly firstNight) =>
        string.Create(CultureInfo.InvariantCulture, $"{UidPrefix}{firstNight:yyyy-MM-dd}");

    /// <summary>
    /// The days of <paramref name="productId"/>, or, when the answer holds days of one product only and it does not carry the id that was asked
    /// for (a unit named by another of its ids), all of them: the call asked for one room, so a single unit in the answer is that room. Days of
    /// several products none of which is the one asked for are not its days: none.
    /// </summary>
    internal static List<OctorateCalendarDay> DaysOf(IReadOnlyCollection<OctorateCalendarDay> days, string productId)
    {
        var own = days.Where(d => string.Equals(d.ProductId, productId, StringComparison.Ordinal)).ToList();
        if (own.Count > 0)
            return own;

        return days.Select(d => d.ProductId).Distinct(StringComparer.Ordinal).Count() == 1 ? [.. days] : [];
    }

    /// <summary>
    /// The closed nights from <paramref name="from"/> to <paramref name="to"/> (both included), in order and once each, less the nights in
    /// <paramref name="takenElsewhere"/>.
    /// </summary>
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

    /// <summary>The runs of consecutive nights of <paramref name="nights"/> (in order, once each): one block for each.</summary>
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

    /// <summary>The nights from <paramref name="start"/> (included) to <paramref name="end"/> (excluded) that fall in [<paramref name="from"/>, <paramref name="to"/>], both included.</summary>
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

/// <summary>A run of closed nights: from <paramref name="FirstNight"/> (included) to <paramref name="CheckOut"/> (excluded).</summary>
internal readonly record struct NightRun(DateOnly FirstNight, DateOnly CheckOut);
