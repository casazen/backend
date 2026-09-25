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
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown supplier KPI period."),
        };
    }
}
