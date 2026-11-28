namespace Casazen.Core.Suppliers;

public enum SupplierKpiPeriod
{
    CurrentMonth,

    PreviousMonth,

    Last30Days,

    CurrentYear,

    Last3Months,
}

public static class SupplierKpiPeriods
{
    public static (DateOnly From, DateOnly To) Resolve(SupplierKpiPeriod period, DateOnly todayInRome)
    {
        var monthStart = new DateOnly(todayInRome.Year, todayInRome.Month, 1);
        return period switch
        {
            SupplierKpiPeriod.CurrentMonth => (monthStart, todayInRome),
            SupplierKpiPeriod.PreviousMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            SupplierKpiPeriod.Last30Days => (todayInRome.AddDays(-29), todayInRome),
            SupplierKpiPeriod.CurrentYear => (new DateOnly(todayInRome.Year, 1, 1), todayInRome),
            SupplierKpiPeriod.Last3Months => (todayInRome.AddMonths(-3).AddDays(1), todayInRome),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown supplier KPI period."),
        };
    }

    /// <summary>
    /// The period the overview of the console compares a period with (SP-04e): the <b>same number of Europe/Rome days</b> that end the
    /// day before it starts, so the two are always of the same length and next to each other (on 16 October, "this month" is
    /// 1-16 October, 16 days, and the period before is 15-30 September, 16 days). Both ends are included.
    /// </summary>
    public static (DateOnly From, DateOnly To) Previous(DateOnly from, DateOnly to)
    {
        if (to < from)
            throw new ArgumentException("The end of a period cannot be before its start.", nameof(to));

        var days = to.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        return (previousTo.AddDays(1 - days), previousTo);
    }

    public static IReadOnlyList<SupplierKpiPeriod> OverviewPeriods { get; } =
    [
        SupplierKpiPeriod.CurrentMonth,
        SupplierKpiPeriod.Last30Days,
        SupplierKpiPeriod.Last3Months,
    ];

    public static string OverviewToken(SupplierKpiPeriod period) => period switch
    {
        SupplierKpiPeriod.CurrentMonth => "current-month",
        SupplierKpiPeriod.Last30Days => "last-30-days",
        SupplierKpiPeriod.Last3Months => "last-3-months",
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Not a period of the overview."),
    };

    public static bool TryParseOverview(string? value, out SupplierKpiPeriod period)
    {
        var text = value?.Trim().Replace('_', '-').ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
        {
            period = SupplierKpiPeriod.CurrentMonth;
            return true;
        }

        foreach (var candidate in OverviewPeriods)
        {
            if (text == OverviewToken(candidate))
            {
                period = candidate;
                return true;
            }
        }

        period = default;
        return false;
    }
}
