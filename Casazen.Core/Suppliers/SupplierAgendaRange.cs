namespace Casazen.Core.Suppliers;

/// <summary>What is wrong with a range of days of the agenda (<see cref="SupplierAgendaRange.Check"/>).</summary>
public enum SupplierRangeProblem
{
    /// <summary>The range is fine.</summary>
    None,

    /// <summary>The last day is before the first.</summary>
    Reversed,

    /// <summary>The range covers more days than the answer allows.</summary>
    TooLong,

    /// <summary>
    /// A day is so close to an end of the calendar (the year 1 or the year 9999) that the arithmetic on days around it (a day more or
    /// less on each side, the days a suggestion looks ahead, the start of a day in UTC) would overflow.
    /// </summary>
    OutOfBounds,
}

/// <summary>
/// The one rule for a range of days that the agenda answers (the <c>from</c> and <c>to</c> of the calendar and of the conflicts):
/// the controller asks it to answer 400, and the services ask it again, so that no caller can make a made-up date
/// (<c>0001-01-01</c>, <c>9999-12-31</c>) reach the arithmetic on days, where it would be an overflow and a 500.
/// </summary>
public static class SupplierAgendaRange
{
    /// <summary>
    /// What is wrong with the range <paramref name="from"/> to <paramref name="to"/> (both included) of at most
    /// <paramref name="maxDays"/> days, or <see cref="SupplierRangeProblem.None"/>. A date at the ends of the calendar is refused
    /// first: nothing is computed from it.
    /// </summary>
    public static SupplierRangeProblem Check(DateOnly from, DateOnly to, int maxDays)
    {
        if (!IsRepresentable(from) || !IsRepresentable(to))
            return SupplierRangeProblem.OutOfBounds;

        if (to < from)
            return SupplierRangeProblem.Reversed;

        return to.DayNumber - from.DayNumber + 1 > maxDays ? SupplierRangeProblem.TooLong : SupplierRangeProblem.None;
    }

    /// <summary>
    /// True when <paramref name="day"/> is far enough from the ends of the calendar
    /// (<see cref="SupplierAgendaLimits.RangeEdgeDays"/> days) for the agenda to do its arithmetic on it. A day that passes is also
    /// safe to add <see cref="SupplierAgendaLimits.DefaultCalendarDays"/> days to.
    /// </summary>
    public static bool IsRepresentable(DateOnly day) =>
        day >= DateOnly.MinValue.AddDays(SupplierAgendaLimits.RangeEdgeDays)
        && day <= DateOnly.MaxValue.AddDays(-SupplierAgendaLimits.RangeEdgeDays);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when <see cref="Check"/> finds a problem: for a service called with a range
    /// that the controller (the only place user input comes in) would have refused with a 400.
    /// </summary>
    public static void Ensure(DateOnly from, DateOnly to, int maxDays)
    {
        switch (Check(from, to, maxDays))
        {
            case SupplierRangeProblem.OutOfBounds:
                throw new ArgumentOutOfRangeException(nameof(from), "The days are too close to an end of the calendar.");
            case SupplierRangeProblem.Reversed:
                throw new ArgumentOutOfRangeException(nameof(to), "The last day cannot be before the first.");
            case SupplierRangeProblem.TooLong:
                throw new ArgumentOutOfRangeException(nameof(to), $"The range covers at most {maxDays} days.");
        }
    }
}
