namespace Casazen.Core.Suppliers;

public static class SupplierServiceWeekdays
{
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

    public static IReadOnlyList<DayOfWeek> FromMask(int mask) =>
        MondayFirst.Where(day => (mask & (1 << Bit(day))) != 0).ToList();

    public static bool IsOffered(int mask, DayOfWeek day) => (mask & (1 << Bit(day))) != 0;

    private static int Bit(DayOfWeek day) => ((int)day + 6) % 7;
}
