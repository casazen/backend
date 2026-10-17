namespace Casazen.Core.Suppliers;

/// <summary>The first and last Europe/Rome day of a window of slots, both included.</summary>
public readonly record struct PublicSlotRange(DateOnly First, DateOnly Last);

/// <summary>
/// Which days a read of the slots of a service covers (SP-09 for the customer, SP-03b for the supplier's own preview "how customers
/// see you"). <b>One definition</b>, so the preview shows the days the public page shows: from the day asked for and never before
/// today, <c>days</c> of them (1 to <see cref="PublicShowcaseLimits.SlotsMaxDays"/>, <see cref="PublicShowcaseLimits.SlotsDefaultDays"/>
/// when left out), cut at the last day that can be booked (<c>today + HorizonDays</c>).
/// </summary>
public static class PublicSlotWindow
{
    /// <summary>
    /// The window, or <c>null</c> when it starts after <paramref name="bookableUntil"/> (an empty window, not an error). The first day
    /// is checked against the horizon <b>before</b> days are added to it, so a made-up date cannot overflow.
    /// </summary>
    /// <param name="today">Today in Europe/Rome.</param>
    /// <param name="bookableUntil">The last day that can be booked: today plus the supplier's horizon.</param>
    /// <param name="from">The first day asked for; <c>null</c>, or a day before today, is today.</param>
    /// <param name="days">How many days; clamped to 1 and <see cref="PublicShowcaseLimits.SlotsMaxDays"/>.</param>
    public static PublicSlotRange? RangeOf(DateOnly today, DateOnly bookableUntil, DateOnly? from, int? days)
    {
        var count = Math.Clamp(days ?? PublicShowcaseLimits.SlotsDefaultDays, 1, PublicShowcaseLimits.SlotsMaxDays);
        var first = from is { } requested && requested > today ? requested : today;
        if (first > bookableUntil)
            return null;

        var last = first.AddDays(count - 1);
        return new PublicSlotRange(first, last > bookableUntil ? bookableUntil : last);
    }
}
