namespace Casazen.Core.Suppliers;

/// <summary>
/// The weekdays a service is offered on, stored as a bit mask in <c>SupplierServiceListing.WeekdaysMask</c>: bit 0 is
/// Monday, bit 6 is Sunday (the order of an Italian week; <see cref="DayOfWeek"/> starts on Sunday, so it is not used
/// as the bit). The API shows the days as <see cref="DayOfWeek"/> names, Monday first.
/// </summary>
public static class SupplierServiceWeekdays
{
    /// <summary>Every day of the week: the default of a service that does not restrict its days.</summary>
    public const int AllMask = 0b111_1111;

    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    /// <summary>The mask of <paramref name="days"/>; duplicates count once, a value that is not a day is ignored.</summary>
    public static int ToMask(IEnumerable<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var mask = 0;
        foreach (var day in days)
        {
            if (Enum.IsDefined(day))
                mask |= 1 << Bit(day);
        }

        return mask;
    }

    /// <summary>The days of <paramref name="mask"/>, Monday first. Bits above Sunday are ignored.</summary>
    public static IReadOnlyList<DayOfWeek> FromMask(int mask) =>
        MondayFirst.Where(day => (mask & (1 << Bit(day))) != 0).ToList();

    /// <summary>True when <paramref name="day"/> is one of the days of <paramref name="mask"/>.</summary>
    public static bool IsOffered(int mask, DayOfWeek day) => (mask & (1 << Bit(day))) != 0;

    private static int Bit(DayOfWeek day) => ((int)day + 6) % 7;
}
