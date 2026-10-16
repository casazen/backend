namespace Casazen.Core.Suppliers;

/// <summary>
/// Period of the supplier work KPIs (SU-11, A4-15): Europe/Rome calendar dates, both ends included, today at most.
/// </summary>
public enum SupplierKpiPeriod
{
    /// <summary>From the 1st of the current month to today.</summary>
    CurrentMonth,

    /// <summary>The whole previous calendar month.</summary>
    PreviousMonth,

    /// <summary>The last 30 days, today included.</summary>
    Last30Days,

    /// <summary>From 1 January of the current year to today.</summary>
    CurrentYear,

    /// <summary>
    /// The last three calendar months, today included (SP-04e): from the day after the same day three months ago to today
    /// (on 16 October, from 17 July).
    /// </summary>
    Last3Months,
}

/// <summary>Calendar dates of a <see cref="SupplierKpiPeriod"/>.</summary>
public static class SupplierKpiPeriods
{
    /// <summary>
    /// First and last Europe/Rome calendar date (both included) of <paramref name="period"/>, for the Rome calendar day
    /// <paramref name="todayInRome"/>.
    /// </summary>
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

    /// <summary>The periods <c>GET api/supplier/dashboard/overview</c> accepts, in the order the console offers them.</summary>
    public static IReadOnlyList<SupplierKpiPeriod> OverviewPeriods { get; } =
    [
        SupplierKpiPeriod.CurrentMonth,
        SupplierKpiPeriod.Last30Days,
        SupplierKpiPeriod.Last3Months,
    ];

    /// <summary>The <c>period</c> value of the overview for <paramref name="period"/>: <c>current-month</c>, <c>last-30-days</c> or <c>last-3-months</c>.</summary>
    public static string OverviewToken(SupplierKpiPeriod period) => period switch
    {
        SupplierKpiPeriod.CurrentMonth => "current-month",
        SupplierKpiPeriod.Last30Days => "last-30-days",
        SupplierKpiPeriod.Last3Months => "last-3-months",
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Not a period of the overview."),
    };

    /// <summary>
    /// The period of the overview named <paramref name="value"/> (<c>current-month</c>, <c>last-30-days</c>, <c>last-3-months</c>; any
    /// case, <c>_</c> or <c>-</c>); a missing value is the current month. False for any other value, including the enum names and numbers.
    /// </summary>
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
