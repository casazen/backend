namespace Casazen.Core.Condominium;

/// <summary>Fiscal-year labels and bounds (CondoLedger <c>fiscal.js</c>). June is the usual start, not January.</summary>
public static class FiscalYear
{
    public readonly record struct Bounds(DateOnly Start, DateOnly End, string Label);

    public static bool IsValidStartMonth(int month) => month is >= 1 and <= 12;

    public static Bounds ForStartYear(int startYear, int startMonth)
    {
        if (!IsValidStartMonth(startMonth))
            throw new ArgumentOutOfRangeException(nameof(startMonth));

        var start = new DateOnly(startYear, startMonth, 1);
        var endYear = startMonth == 1 ? startYear : startYear + 1;
        var endMonth = startMonth == 1 ? 12 : startMonth - 1;
        var end = new DateOnly(endYear, endMonth, DateTime.DaysInMonth(endYear, endMonth));
        var label = startMonth == 1 ? startYear.ToString() : $"{startYear}/{startYear + 1}";
        return new Bounds(start, end, label);
    }

    /// <summary>Accepts <c>2024/2025</c> or <c>2025</c> (a calendar year when the fiscal year starts in January).</summary>
    public static bool TryParseLabel(string? text, int startMonth, out Bounds bounds, out string? error)
    {
        bounds = default;
        error = null;
        var label = (text ?? string.Empty).Trim();
        if (!IsValidStartMonth(startMonth))
        {
            error = "month";
            return false;
        }

        var split = System.Text.RegularExpressions.Regex.Match(label, @"^(\d{4})/(\d{4})$");
        if (split.Success)
        {
            var startYear = int.Parse(split.Groups[1].Value);
            var endYear = int.Parse(split.Groups[2].Value);
            if (endYear != startYear + 1)
            {
                error = "span";
                return false;
            }

            bounds = ForStartYear(startYear, startMonth);
            if (!string.Equals(bounds.Label, label, StringComparison.Ordinal))
            {
                error = "label";
                return false;
            }

            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(label, @"^\d{4}$"))
        {
            bounds = ForStartYear(int.Parse(label), startMonth);
            return true;
        }

        error = "format";
        return false;
    }

    public static bool Overlaps(DateOnly start, DateOnly end, DateOnly otherStart, DateOnly otherEnd) =>
        start <= otherEnd && otherStart <= end;

    /// <summary>
    /// Due date of a conguaglio paid on its own: the last day of the third month of the year.
    /// A June start is due on 31 August.
    /// </summary>
    public static DateOnly SeparateBalanceDueDate(DateOnly periodStart) =>
        periodStart.AddMonths(3).AddDays(-1);
}
