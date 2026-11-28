namespace Casazen.Core.Suppliers;

public enum SupplierRangeProblem
{
    None,

    Reversed,

    TooLong,

    OutOfBounds,
}

/// <summary>
/// The one rule for a range of days that the agenda answers (the <c>from</c> and <c>to</c> of the calendar and of the conflicts):
/// the controller asks it to answer 400, and the services ask it again, so that no caller can make a made-up date
/// (<c>0001-01-01</c>, <c>9999-12-31</c>) reach the arithmetic on days, where it would be an overflow and a 500.
/// </summary>
public static class SupplierAgendaRange
{
    public static SupplierRangeProblem Check(DateOnly from, DateOnly to, int maxDays)
    {
        if (!IsRepresentable(from) || !IsRepresentable(to))
            return SupplierRangeProblem.OutOfBounds;

        if (to < from)
            return SupplierRangeProblem.Reversed;

        return to.DayNumber - from.DayNumber + 1 > maxDays ? SupplierRangeProblem.TooLong : SupplierRangeProblem.None;
    }

    public static bool IsRepresentable(DateOnly day) =>
        day >= DateOnly.MinValue.AddDays(SupplierAgendaLimits.RangeEdgeDays)
        && day <= DateOnly.MaxValue.AddDays(-SupplierAgendaLimits.RangeEdgeDays);

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
