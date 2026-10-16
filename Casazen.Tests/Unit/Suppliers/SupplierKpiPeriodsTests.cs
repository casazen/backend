using System.Globalization;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SU-11 (A4-15): calendar dates of the supplier KPI periods, for a Europe/Rome "today".</summary>
public class SupplierKpiPeriodsTests
{
    [Theory]
    [InlineData(SupplierKpiPeriod.CurrentMonth, "2026-09-25", "2026-09-01", "2026-09-25")]
    [InlineData(SupplierKpiPeriod.CurrentMonth, "2026-09-01", "2026-09-01", "2026-09-01")]
    [InlineData(SupplierKpiPeriod.PreviousMonth, "2026-09-25", "2026-08-01", "2026-08-31")]
    [InlineData(SupplierKpiPeriod.PreviousMonth, "2026-03-10", "2026-02-01", "2026-02-28")]
    [InlineData(SupplierKpiPeriod.PreviousMonth, "2026-01-15", "2025-12-01", "2025-12-31")]
    [InlineData(SupplierKpiPeriod.Last30Days, "2026-09-25", "2026-08-27", "2026-09-25")]
    [InlineData(SupplierKpiPeriod.CurrentYear, "2026-09-25", "2026-01-01", "2026-09-25")]
    public void Resolve_Period_ReturnsItsRomeCalendarDates(SupplierKpiPeriod period, string today, string from, string to)
    {
        var (actualFrom, actualTo) = SupplierKpiPeriods.Resolve(period, Date(today));

        Assert.Equal(Date(from), actualFrom);
        Assert.Equal(Date(to), actualTo);
    }

    [Fact]
    public void Resolve_UndefinedPeriod_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SupplierKpiPeriods.Resolve((SupplierKpiPeriod)99, new DateOnly(2026, 9, 25)));
    }

    // ─── SP-04e: the last three months, the period before, the periods of the overview ───

    [Theory]
    [InlineData("2026-10-16", "2026-07-17", "2026-10-16")]
    [InlineData("2026-10-01", "2026-07-02", "2026-10-01")]
    [InlineData("2026-05-31", "2026-03-01", "2026-05-31")] // 31 May - 3 months = 28 February, the day after is 1 March
    [InlineData("2026-03-31", "2026-01-01", "2026-03-31")]
    [InlineData("2026-02-28", "2025-11-29", "2026-02-28")]
    [InlineData("2024-02-29", "2023-11-30", "2024-02-29")] // leap day
    [InlineData("2026-01-15", "2025-10-16", "2026-01-15")] // across the year
    public void Resolve_Last3Months_IsTheLastThreeCalendarMonthsTodayIncluded(string today, string from, string to)
    {
        var (actualFrom, actualTo) = SupplierKpiPeriods.Resolve(SupplierKpiPeriod.Last3Months, Date(today));

        Assert.Equal(Date(from), actualFrom);
        Assert.Equal(Date(to), actualTo);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-16", "2026-09-15", "2026-09-30")] // this month on the 16th: 16 days, the 16 before
    [InlineData("2026-10-01", "2026-10-01", "2026-09-30", "2026-09-30")] // the first day of the month: one day, the one before
    [InlineData("2026-09-17", "2026-10-16", "2026-08-18", "2026-09-16")] // 30 days
    [InlineData("2026-07-17", "2026-10-16", "2026-04-16", "2026-07-16")] // 92 days across three months
    [InlineData("2026-03-01", "2026-03-31", "2026-01-29", "2026-02-28")] // across February
    [InlineData("2026-01-01", "2026-01-10", "2025-12-22", "2025-12-31")] // across the year
    public void Previous_IsTheSameNumberOfDaysEndingTheDayBefore(string from, string to, string expectedFrom, string expectedTo)
    {
        var (previousFrom, previousTo) = SupplierKpiPeriods.Previous(Date(from), Date(to));

        Assert.Equal(Date(expectedFrom), previousFrom);
        Assert.Equal(Date(expectedTo), previousTo);
    }

    [Fact]
    public void Previous_EveryDayOfTwoYears_AllThreePeriods_AreAdjacentAndOfTheSameLength()
    {
        // The invariant the console relies on to say "better" or "worse": no gap, no overlap, the same length.
        for (var today = new DateOnly(2026, 1, 1); today <= new DateOnly(2027, 12, 31); today = today.AddDays(1))
        {
            foreach (var period in SupplierKpiPeriods.OverviewPeriods)
            {
                var (from, to) = SupplierKpiPeriods.Resolve(period, today);
                var (previousFrom, previousTo) = SupplierKpiPeriods.Previous(from, to);

                Assert.True(to == today, $"{period} on {today}: the period ends today");
                Assert.True(previousTo.AddDays(1) == from, $"{period} on {today}: the previous period ends the day before");
                Assert.True(
                    previousTo.DayNumber - previousFrom.DayNumber == to.DayNumber - from.DayNumber,
                    $"{period} on {today}: the same length");
            }
        }
    }

    [Fact]
    public void Previous_AnEndBeforeTheStart_Throws()
    {
        Assert.Throws<ArgumentException>(() => SupplierKpiPeriods.Previous(new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 15)));
    }

    [Fact]
    public void OverviewPeriods_AreTheThreeTheConsoleOffers_InThatOrder()
    {
        Assert.Equal(
            new[] { SupplierKpiPeriod.CurrentMonth, SupplierKpiPeriod.Last30Days, SupplierKpiPeriod.Last3Months },
            SupplierKpiPeriods.OverviewPeriods);
        Assert.Equal(
            new[] { "current-month", "last-30-days", "last-3-months" },
            SupplierKpiPeriods.OverviewPeriods.Select(SupplierKpiPeriods.OverviewToken));
    }

    [Theory]
    [InlineData("current-month", SupplierKpiPeriod.CurrentMonth)]
    [InlineData("last-30-days", SupplierKpiPeriod.Last30Days)]
    [InlineData("last-3-months", SupplierKpiPeriod.Last3Months)]
    [InlineData("Last-3-Months", SupplierKpiPeriod.Last3Months)]
    [InlineData(" last_30_days ", SupplierKpiPeriod.Last30Days)]
    [InlineData(null, SupplierKpiPeriod.CurrentMonth)]
    [InlineData("", SupplierKpiPeriod.CurrentMonth)]
    [InlineData("   ", SupplierKpiPeriod.CurrentMonth)]
    public void TryParseOverview_KnownValueOrNothing_ReturnsThePeriod(string? value, SupplierKpiPeriod expected)
    {
        Assert.True(SupplierKpiPeriods.TryParseOverview(value, out var period));
        Assert.Equal(expected, period);
    }

    [Theory]
    [InlineData("CurrentMonth")]
    [InlineData("PreviousMonth")]
    [InlineData("CurrentYear")]
    [InlineData("last-year")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("current-month,last-30-days")]
    public void TryParseOverview_AnythingElse_IsRefused(string value)
    {
        Assert.False(SupplierKpiPeriods.TryParseOverview(value, out _));
    }

    [Theory]
    [InlineData(SupplierKpiPeriod.PreviousMonth)]
    [InlineData(SupplierKpiPeriod.CurrentYear)]
    public void OverviewToken_APeriodTheOverviewDoesNotOffer_Throws(SupplierKpiPeriod period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierKpiPeriods.OverviewToken(period));
    }

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
