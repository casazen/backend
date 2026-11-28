using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03b: the rules behind the numbers of the Home that need no database. The free period, the difference from the period before,
/// and the chart: its bars (ISO weeks from Monday or months, cut at the edges of the period), the revenue of each (pro rata per
/// night), and the cents, which must add up to the revenue of the period whatever the rounding. Real dates of October 2026, the
/// month the clocks go back (25 October).
/// </summary>
public class HostOverviewRulesTests
{
    private static DateTime Day(int month, int day, int year = 2026) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static HostRevenueStay Stay(decimal basePrice, DateTime checkIn, DateTime checkOut) =>
        new(basePrice, checkIn, checkOut, BookingSource.Direct);

    private static HostDashboardPeriod Custom(DateTime first, DateTime last)
    {
        Assert.True(HostDashboardPeriod.TryCustom(DateOnly.FromDateTime(first), DateOnly.FromDateTime(last), out var period));
        return period;
    }

    [Fact]
    public void TryCustom_BothDaysAreIncluded_AndTheNightsAreTheDaysBetween()
    {
        Assert.True(HostDashboardPeriod.TryCustom(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 11), out var week));
        Assert.True(HostDashboardPeriod.TryCustom(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9), out var oneDay));

        Assert.Equal(HostDashboardPeriodKind.Custom, week.Kind);
        Assert.Equal((Day(10, 5), Day(10, 12), 7), (week.From, week.To, week.Nights));
        Assert.Equal((Day(10, 9), Day(10, 10), 1), (oneDay.From, oneDay.To, oneDay.Nights));
    }

    [Theory]
    [InlineData(2026, 1, 1, 2026, 12, 31, true)]
    [InlineData(2024, 1, 1, 2024, 12, 31, true)]
    [InlineData(2026, 1, 1, 2027, 1, 1, true)]
    [InlineData(2024, 1, 1, 2025, 1, 1, false)]
    [InlineData(2026, 10, 11, 2026, 10, 5, false)]
    [InlineData(1999, 12, 1, 2000, 1, 31, false)]
    [InlineData(2000, 1, 1, 2000, 1, 31, true)]
    [InlineData(2100, 12, 1, 2100, 12, 31, true)]
    [InlineData(2100, 12, 1, 2101, 1, 31, false)]
    public void TryCustom_AtMost366Days_InOrder_InTheSupportedYears(
        int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay, bool accepted)
    {
        var ok = HostDashboardPeriod.TryCustom(
            new DateOnly(fromYear, fromMonth, fromDay), new DateOnly(toYear, toMonth, toDay), out _);

        Assert.Equal(accepted, ok);
    }

    [Fact]
    public void TryCustom_ThePeriodBeforeIsTheSameNumberOfNightsEndingWhereItStarts()
    {
        var period = Custom(Day(10, 19), Day(11, 8));

        var previous = period.Previous();

        Assert.Equal(21, period.Nights);
        Assert.Equal((HostDashboardPeriodKind.Custom, Day(9, 28), Day(10, 19), 21), (previous.Kind, previous.From, previous.To, previous.Nights));
    }

    [Theory]
    [InlineData(79, 100, 76, 100, 3.0)]
    [InlineData(50, 100, 75, 100, -25.0)]
    [InlineData(9, 62, 5, 62, 6.5)]
    [InlineData(1, 3, 1, 3, 0.0)]
    public void OccupancyDeltaPoints_IsTheDifferenceOfTheRatesInPoints_ToOneDecimal(
        int occupied, int available, int previousOccupied, int previousAvailable, double expected)
    {
        var delta = HostOverviewRules.OccupancyDeltaPoints(
            new HostDashboardOccupancy(occupied, available, 0), new HostDashboardOccupancy(previousOccupied, previousAvailable, 0));

        Assert.Equal((decimal)expected, delta);
    }

    [Fact]
    public void OccupancyDeltaPoints_WhenEitherPeriodHasNothingAvailable_HasNoDifference()
    {
        var some = new HostDashboardOccupancy(5, 10, 0);
        var none = new HostDashboardOccupancy(0, 0, 0);

        Assert.Null(HostOverviewRules.OccupancyDeltaPoints(some, none));
        Assert.Null(HostOverviewRules.OccupancyDeltaPoints(none, some));
        Assert.Null(HostOverviewRules.OccupancyDeltaPoints(none, none));
    }

    [Theory]
    [InlineData(1100.00, 1000.00, 10.0)]
    [InlineData(900.00, 1000.00, -10.0)]
    [InlineData(123.45, 100.00, 23.5)]
    [InlineData(0.00, 500.00, -100.0)]
    [InlineData(600.00, 500.00, 20.0)]
    public void RevenueDeltaPercent_IsMoreOrLessThanTheEarlierPeriod_InPercentToOneDecimal(double now, double before, double expected)
    {
        Assert.Equal((decimal)expected, HostOverviewRules.RevenueDeltaPercent((decimal)now, (decimal)before));
    }

    [Fact]
    public void RevenueDeltaPercent_AgainstNothing_IsNothing()
    {
        Assert.Null(HostOverviewRules.RevenueDeltaPercent(600m, 0m));
        Assert.Null(HostOverviewRules.RevenueDeltaPercent(0m, 0m));
    }

    [Theory]
    [InlineData(1, HostOverviewSeriesUnit.Week)]
    [InlineData(30, HostOverviewSeriesUnit.Week)]
    [InlineData(31, HostOverviewSeriesUnit.Week)]
    [InlineData(62, HostOverviewSeriesUnit.Week)]
    [InlineData(63, HostOverviewSeriesUnit.Month)]
    [InlineData(92, HostOverviewSeriesUnit.Month)]
    [InlineData(366, HostOverviewSeriesUnit.Month)]
    public void UnitFor_WeeksUpToNineOfThem_MonthsBeyond(int nights, HostOverviewSeriesUnit expected)
    {
        var first = Day(1, 1, 2024);
        var period = new HostDashboardPeriod(HostDashboardPeriodKind.Custom, first, first.AddDays(nights));

        Assert.Equal(expected, HostOverviewSeriesRules.UnitFor(period));
    }

    [Fact]
    public void BucketsOf_AMonth_IsISOWeeksFromMonday_CutAtTheEdgesOfThePeriod()
    {
        var october = HostDashboardPeriod.ForMonth(new DateOnly(2026, 10, 1));

        var buckets = HostOverviewSeriesRules.BucketsOf(october, HostOverviewSeriesRules.UnitFor(october));

        Assert.Equal(
            [
                ("2026-W40", Day(10, 1), Day(10, 5)),
                ("2026-W41", Day(10, 5), Day(10, 12)),
                ("2026-W42", Day(10, 12), Day(10, 19)),
                ("2026-W43", Day(10, 19), Day(10, 26)),
                ("2026-W44", Day(10, 26), Day(11, 1)),
            ],
            buckets.Select(b => (b.Label, b.From, b.To)));
    }

    [Fact]
    public void BucketsOf_AWeekAcrossTheEndOfTheYear_TakesTheISOYearOfItsWeek()
    {
        var period = Custom(Day(12, 29, 2025), Day(1, 4));

        var buckets = HostOverviewSeriesRules.BucketsOf(period, HostOverviewSeriesUnit.Week);

        var week = Assert.Single(buckets);
        Assert.Equal(("2026-W01", Day(12, 29, 2025), Day(1, 5)), (week.Label, week.From, week.To));
    }

    [Fact]
    public void BucketsOf_ThreeMonths_IsWholeMonths_TheFirstAndLastCutAtThePeriod()
    {
        var period = Custom(Day(7, 15), Day(9, 20));

        var buckets = HostOverviewSeriesRules.BucketsOf(period, HostOverviewSeriesUnit.Month);

        Assert.Equal(
            [("2026-07", Day(7, 15), Day(8, 1)), ("2026-08", Day(8, 1), Day(9, 1)), ("2026-09", Day(9, 1), Day(9, 21))],
            buckets.Select(b => (b.Label, b.From, b.To)));
    }

    [Theory]
    [InlineData(2026, 10, 1, 2026, 10, 31)]
    [InlineData(2026, 10, 19, 2026, 11, 8)]
    [InlineData(2026, 3, 22, 2026, 4, 5)]
    [InlineData(2025, 12, 20, 2026, 1, 20)]
    [InlineData(2026, 10, 25, 2026, 10, 25)]
    [InlineData(2026, 1, 1, 2026, 12, 31)]
    public void BucketsOf_CoverThePeriodExactly_InOrder_WithNoGapAndNoOverlap(
        int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        var period = Custom(Day(fromMonth, fromDay, fromYear), Day(toMonth, toDay, toYear));

        foreach (var unit in Enum.GetValues<HostOverviewSeriesUnit>())
        {
            var buckets = HostOverviewSeriesRules.BucketsOf(period, unit);

            Assert.Equal(period.From, buckets[0].From);
            Assert.Equal(period.To, buckets[^1].To);
            for (var i = 1; i < buckets.Count; i++)
                Assert.Equal(buckets[i - 1].To, buckets[i].From);
            Assert.All(buckets, b => Assert.True(b.To > b.From));
            Assert.Equal(period.Nights, buckets.Sum(b => (b.To - b.From).Days));
        }
    }

    [Fact]
    public void Distribute_EachNightIsPaidWhereItFalls_ProRataOfTheBasePrice()
    {
        var windows = new[] { (Day(10, 19), Day(10, 26)), (Day(10, 26), Day(11, 2)) };
        var stays = new[] { Stay(500m, Day(10, 23), Day(10, 28)) };

        var amounts = HostOverviewSeriesRules.Distribute(stays, windows, total: 500m);

        Assert.Equal([300m, 200m], amounts);
    }

    [Fact]
    public void Distribute_TheCentsAddUpToTheTotal_WhateverTheRounding()
    {
        var windows = new[] { (Day(10, 5), Day(10, 6)), (Day(10, 6), Day(10, 7)), (Day(10, 7), Day(10, 8)) };
        var stays = new[] { Stay(100m, Day(10, 5), Day(10, 8)) };

        var amounts = HostOverviewSeriesRules.Distribute(stays, windows, total: 100m);

        Assert.Equal(100m, amounts.Sum());
        Assert.Equal([33.33m, 33.34m, 33.33m], amounts);
        Assert.All(amounts, amount => Assert.InRange(Math.Abs(amount - 100m / 3m), 0m, 0.01m));
    }

    [Fact]
    public void Distribute_NoBarIsEverNegative_EvenWhenTheLastOnesAreEmptyAndTheEarlierOnesRoundUp()
    {
        var windows = new[]
        {
            (Day(10, 5), Day(10, 6)), (Day(10, 6), Day(10, 7)), (Day(10, 7), Day(10, 14)), (Day(10, 14), Day(10, 21)), (Day(10, 21), Day(10, 28)),
        };
        var stays = new[] { Stay(100.05m, Day(10, 5), Day(10, 7)) };

        var amounts = HostOverviewSeriesRules.Distribute(stays, windows, total: 100.05m);

        Assert.All(amounts, amount => Assert.True(amount >= 0m, $"{amount}"));
        Assert.Equal(100.05m, amounts.Sum());
        Assert.Equal([50.03m, 50.02m, 0m, 0m, 0m], amounts);
    }

    [Fact]
    public void Distribute_ManyStaysAndPrices_AlwaysAddUpToTheRevenueOfThePeriod()
    {
        var period = Custom(Day(10, 1), Day(10, 31));
        var random = new Random(20261016);
        var stays = Enumerable.Range(0, 60)
            .Select(_ =>
            {
                var start = Day(9, 20).AddDays(random.Next(0, 50));
                return Stay(Math.Round((decimal)(random.NextDouble() * 900 + 40), 2), start, start.AddDays(random.Next(1, 14)));
            })
            .ToList();
        var revenue = StayKpiRules.RevenueOf(stays, period.From, period.To);
        var windows = HostOverviewSeriesRules.BucketsOf(period, HostOverviewSeriesUnit.Week).Select(b => (b.From, b.To)).ToList();

        var amounts = HostOverviewSeriesRules.Distribute(stays, windows, revenue);

        Assert.Equal(revenue, amounts.Sum());
        Assert.All(amounts, amount => Assert.True(amount >= 0m));
        Assert.True(revenue > 0m);
    }

    [Fact]
    public void Build_TheBarsAddUpToTheRevenue_ThePreviousBarsToThePreviousRevenue_AndTheFutureOnesAreForecasts()
    {
        var october = HostDashboardPeriod.ForMonth(new DateOnly(2026, 10, 1));
        var stays = new[]
        {
            Stay(300m, Day(10, 10), Day(10, 13)),
            Stay(300m, Day(10, 8), Day(10, 11)),
        };
        var previousStays = new[] { Stay(500m, Day(9, 20), Day(9, 25)) };
        var revenue = StayKpiRules.RevenueOf(stays, october.From, october.To);
        var previous = october.Previous();
        var previousRevenue = StayKpiRules.RevenueOf(previousStays, previous.From, previous.To);

        var series = HostOverviewSeriesRules.Build(october, Day(10, 9), stays, revenue, previousStays, previousRevenue);

        Assert.Equal(HostOverviewSeriesUnit.Week, series.Unit);
        Assert.Equal(600m, revenue);
        Assert.Equal(
            [("2026-W40", 0m), ("2026-W41", 500m), ("2026-W42", 100m), ("2026-W43", 0m), ("2026-W44", 0m)],
            series.Buckets.Select(b => (b.Label, b.Amount)));
        Assert.Equal(revenue, series.Buckets.Sum(b => b.Amount));
        Assert.Equal([0m, 0m, 0m, 500m, 0m], series.Buckets.Select(b => b.PreviousAmount));
        Assert.Equal(previousRevenue, series.Buckets.Sum(b => b.PreviousAmount));
        Assert.Equal([false, false, true, true, true], series.Buckets.Select(b => b.IsFuture));
    }

    [Fact]
    public void Build_TheBarThatStartsTodayInRomeIsNotAForecast_EvenWhenTheClockOfTheServerIsStillYesterday()
    {
        var today = RomeCalendar.TodayAt(new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero));
        var period = Custom(Day(10, 19), Day(11, 8));

        var series = HostOverviewSeriesRules.Build(period, today, [], 0m, [], 0m);

        Assert.Equal(Day(10, 26), today);
        Assert.Equal(
            [("2026-W43", false), ("2026-W44", false), ("2026-W45", true)],
            series.Buckets.Select(b => (b.Label, b.IsFuture)));
    }

    [Fact]
    public void Build_ABarEndsWhereTheNextStarts_AndAMonthSeriesAddsUpToo()
    {
        var quarter = Custom(Day(7, 1), Day(9, 30));
        var stays = new[] { Stay(1000m, Day(7, 28), Day(8, 4)), Stay(900m, Day(9, 28), Day(10, 5)) };
        var revenue = StayKpiRules.RevenueOf(stays, quarter.From, quarter.To);

        var series = HostOverviewSeriesRules.Build(quarter, Day(10, 9), stays, revenue, [], 0m);

        Assert.Equal(HostOverviewSeriesUnit.Month, series.Unit);
        Assert.Equal(["2026-07", "2026-08", "2026-09"], series.Buckets.Select(b => b.Label));
        Assert.Equal(revenue, series.Buckets.Sum(b => b.Amount));
        Assert.Equal([571.43m, 428.57m, 385.71m], series.Buckets.Select(b => b.Amount));
        Assert.All(series.Buckets, b => Assert.Equal(0m, b.PreviousAmount));
        Assert.All(series.Buckets, b => Assert.False(b.IsFuture));
    }
}
