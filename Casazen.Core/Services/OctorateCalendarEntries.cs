using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>What of a property the prices of its nights are made of (<see cref="OctorateCalendarEntries.Build"/>).</summary>
/// <param name="NightlyRate">The nightly rate of the property.</param>
/// <param name="WeekendSurchargePercent">The weekend surcharge of the property, in percent (0 for none).</param>
/// <param name="MinNights">The minimum stay of the property; <c>null</c> for none.</param>
public sealed record OctorateCalendarProperty(decimal NightlyRate, decimal WeekendSurchargePercent, int? MinNights);

/// <summary>
/// From the nights of a connected property to the values the channel manager must hold (E08/F05/US02/T01, NF-36, PO-16). Pure: no clock, no database,
/// no call. CasaZen is the sole writer of a connected property, so what it sends for a night is always the <b>effective</b> value of that night: a rule that
/// was cleared sends the usual value again.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Price.</b> <see cref="StayPricing.NightPrice"/>, the function the quote adds the nights of a stay up with, fed with the prices the host set for
/// dates, so the channel manager holds the price a guest is quoted, to the cent.</item>
/// <item><b>Stay.</b> The minimum stay is the rule, else the minimum of the property, else 1. The maximum is the rule; with none, the value of
/// <c>Octorate:Calendar:NoMaxStay</c> when it is configured, else the field is left out. The maximum advance has no field on the channel manager and is
/// never sent; the minimum advance is the <c>cutOffDays</c> of the night (0 when none).</item>
/// <item><b>Closed nights.</b> A night a manual block or a mode-change block closes is sent closed, with <c>stopSells</c> or with <c>availability</c> 0
/// as <c>Octorate:Calendar:CloseNightsWith</c> says; any other night is sent open (<c>stopSells</c> false, or <c>availability</c> 1). A night something
/// else holds (a booking, a hold, a block of a channel) is not ours to open or to close: the field is left out, so a stay that CasaZen or the channel
/// manager took is never reopened by a change of a price.</item>
/// <item><b>Intervals.</b> Adjacent nights with equal values become one interval.</item>
/// </list>
/// </remarks>
public static class OctorateCalendarEntries
{
    /// <summary>
    /// The entries for the nights from <paramref name="from"/> to <paramref name="toInclusive"/> (both included), one for each run of adjacent nights with
    /// equal values, in order.
    /// </summary>
    /// <param name="property">The rate, the weekend surcharge and the minimum stay of the property.</param>
    /// <param name="target">Where the property is on the channel manager: the room of the entries.</param>
    /// <param name="rules">The rules of the nights of the window, by night (only the nights that have one).</param>
    /// <param name="hostPrices">The prices the host set for dates (<see cref="DayRulePricing.HostPrices"/>): the seasonal prices it confirmed and the prices of the rules.</param>
    /// <param name="closedNights">The nights a manual block or a mode-change block closes.</param>
    /// <param name="heldNights">The nights something other than those blocks holds (the sale of the night is not written).</param>
    /// <param name="options">The way the channel manager is closed and the value of «no maximum stay».</param>
    public static IReadOnlyList<OctorateCalendarUpdate> Build(
        OctorateCalendarProperty property,
        OctorateBookingTarget target,
        DateOnly from,
        DateOnly toInclusive,
        IReadOnlyDictionary<DateOnly, DayRuleValues> rules,
        IReadOnlyDictionary<DateOnly, decimal> hostPrices,
        IReadOnlySet<DateOnly> closedNights,
        IReadOnlySet<DateOnly> heldNights,
        OctorateCalendarOptions options)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(hostPrices);
        ArgumentNullException.ThrowIfNull(closedNights);
        ArgumentNullException.ThrowIfNull(heldNights);
        ArgumentNullException.ThrowIfNull(options);

        var entries = new List<OctorateCalendarUpdate>();
        OctorateCalendarValues? current = null;
        var runStart = from;
        for (var night = from; night <= toInclusive; night = night.AddDays(1))
        {
            var values = ValuesOf(night, property, rules, hostPrices, closedNights, heldNights, options);
            if (current is not null && values == current)
                continue;

            if (current is not null)
                entries.Add(new OctorateCalendarUpdate(target.ProductId, runStart, night.AddDays(-1), current));

            current = values;
            runStart = night;
        }

        if (current is not null)
            entries.Add(new OctorateCalendarUpdate(target.ProductId, runStart, toInclusive, current));

        return entries;
    }

    private static OctorateCalendarValues ValuesOf(
        DateOnly night,
        OctorateCalendarProperty property,
        IReadOnlyDictionary<DateOnly, DayRuleValues> rules,
        IReadOnlyDictionary<DateOnly, decimal> hostPrices,
        IReadOnlySet<DateOnly> closedNights,
        IReadOnlySet<DateOnly> heldNights,
        OctorateCalendarOptions options)
    {
        rules.TryGetValue(night, out var rule);
        var price = StayPricing.NightPrice(night, property.NightlyRate, property.WeekendSurchargePercent, hostPrices);

        var closed = closedNights.Contains(night);
        var held = !closed && heldNights.Contains(night);
        int? availability = null;
        bool? stopSells = null;
        if (!held)
        {
            if (options.CloseNightsWith == OctorateCloseNightsWith.Availability)
                availability = closed ? 0 : 1;
            else
                stopSells = closed;
        }

        return new OctorateCalendarValues(
            Availability: availability,
            Price: price,
            MinStay: DayRuleDefaults.MinNightsOf(rule, property.MinNights),
            MaxStay: rule?.MaxNights ?? options.NoMaxStayNights,
            StopSells: stopSells,
            CloseToArrival: rule?.ClosedToArrival ?? false,
            CloseToDeparture: rule?.ClosedToDeparture ?? false,
            CutOffDays: rule?.MinAdvanceDays ?? 0);
    }
}
