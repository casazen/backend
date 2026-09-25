using Casazen.Core.Pricing;
using Xunit;

namespace Casazen.Tests.Unit.Pricing;

/// <summary>PC-15 (D4, A2-14, A2-34): official Italian holidays, seasonal rules on the real price, date-based schedule.</summary>
public class ItalianPublicHolidaysTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)] // latest possible date
    [InlineData(2285, 3, 22)] // earliest possible date
    public void EasterSunday_Year_IsTheGregorianEaster(int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), ItalianPublicHolidays.EasterSunday(year));
    }

    [Theory]
    [InlineData(2026, 4, 6)]
    [InlineData(2027, 3, 29)]
    public void On_DayAfterEaster_IsEasterMonday(int year, int month, int day)
    {
        Assert.Equal(ItalianHoliday.EasterMonday, ItalianPublicHolidays.On(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(1, 1, ItalianHoliday.NewYear)]
    [InlineData(1, 6, ItalianHoliday.Epiphany)]
    [InlineData(4, 25, ItalianHoliday.Liberation)]
    [InlineData(5, 1, ItalianHoliday.Labour)]
    [InlineData(6, 2, ItalianHoliday.Republic)]
    [InlineData(8, 15, ItalianHoliday.Assumption)]
    [InlineData(10, 4, ItalianHoliday.SaintFrancis)]
    [InlineData(11, 1, ItalianHoliday.AllSaints)]
    [InlineData(12, 8, ItalianHoliday.ImmaculateConception)]
    [InlineData(12, 25, ItalianHoliday.Christmas)]
    [InlineData(12, 26, ItalianHoliday.SaintStephen)]
    public void On_FixedNationalHoliday2026_IsRecognised(int month, int day, ItalianHoliday expected)
    {
        Assert.Equal(expected, ItalianPublicHolidays.On(new DateOnly(2026, month, day)));
    }

    [Fact]
    public void ForYear_2026_HasThirteenNationalHolidaysWithEaster()
    {
        var holidays = ItalianPublicHolidays.ForYear(2026);

        Assert.Equal(13, holidays.Count);
        Assert.Contains((new DateOnly(2026, 4, 5), ItalianHoliday.EasterSunday), holidays);
        Assert.Equal(holidays.OrderBy(h => h.Date), holidays);
    }

    [Fact]
    public void On_FourthOctoberBefore2026_IsNotAHoliday()
    {
        // Legge 151/2025: national holiday from 2026 only.
        Assert.Null(ItalianPublicHolidays.On(new DateOnly(2025, 10, 4)));
        Assert.Equal(12, ItalianPublicHolidays.ForYear(2025).Count);
    }

    [Theory]
    [InlineData(2026, 3, 19)] // San Giuseppe: not a national holiday
    [InlineData(2026, 6, 29)] // Santi Pietro e Paolo: patron of Rome only
    [InlineData(2026, 12, 24)]
    [InlineData(2026, 7, 15)]
    public void On_OrdinaryOrLocalDay_IsNull(int year, int month, int day)
    {
        Assert.Null(ItalianPublicHolidays.On(new DateOnly(year, month, day)));
    }
}

public class SeasonalPriceCalculatorTests
{
    private static readonly SeasonalPricingRules Example = SeasonalPricingRules.Example;

    [Fact]
    public void Suggest_PropertyAt180InSummer_Is180TimesHighSeasonRule()
    {
        var day = SeasonalPriceCalculator.Suggest(new DateOnly(2026, 7, 14), 180m, Example);

        Assert.Equal(180m, day.BasePrice);
        Assert.Equal(180m * SeasonalPricingRules.ExampleHighSeasonMultiplier, day.SuggestedPrice);
        Assert.Equal(234m, day.SuggestedPrice);
        Assert.Equal(SeasonalPriceRule.HighSeason, day.Rule);
        Assert.Equal(1.30m, day.Multiplier);
        Assert.Null(day.Holiday);
    }

    [Fact]
    public void Suggest_HolidayInHighSeason_AppliesOnlyTheHolidayRule()
    {
        var day = SeasonalPriceCalculator.Suggest(new DateOnly(2026, 8, 15), 180m, Example);

        Assert.Equal(270m, day.SuggestedPrice); // 180 x 1.50, not 180 x 1.50 x 1.30
        Assert.Equal(SeasonalPriceRule.Holiday, day.Rule);
        Assert.Equal(ItalianHoliday.Assumption, day.Holiday);
    }

    [Fact]
    public void Suggest_LowSeason_Is180TimesLowSeasonRule()
    {
        var day = SeasonalPriceCalculator.Suggest(new DateOnly(2026, 11, 10), 180m, Example);

        Assert.Equal(144m, day.SuggestedPrice);
        Assert.Equal(SeasonalPriceRule.LowSeason, day.Rule);
    }

    [Fact]
    public void Suggest_ShoulderMonth_KeepsTheBasePrice()
    {
        var day = SeasonalPriceCalculator.Suggest(new DateOnly(2026, 9, 15), 180m, Example);

        Assert.Equal(180m, day.SuggestedPrice);
        Assert.Equal(1m, day.Multiplier);
        Assert.Equal(SeasonalPriceRule.None, day.Rule);
    }

    [Fact]
    public void Suggest_EasterWithHolidaysOff_UsesTheSeasonOnly()
    {
        var rules = Example with { IncludePublicHolidays = false, HighSeasonMonths = [4] };

        var day = SeasonalPriceCalculator.Suggest(new DateOnly(2026, 4, 5), 180m, rules);

        Assert.Equal(SeasonalPriceRule.HighSeason, day.Rule);
        Assert.Equal(234m, day.SuggestedPrice);
    }

    [Fact]
    public void Suggest_HostRules_UsesTheHostMonthsAndMultiplier()
    {
        var rules = Example with { HighSeasonMonths = [9], HighSeasonMultiplier = 1.15m, IncludeSeasonality = true };

        Assert.Equal(207m, SeasonalPriceCalculator.Suggest(new DateOnly(2026, 9, 10), 180m, rules).SuggestedPrice);
        Assert.Equal(180m, SeasonalPriceCalculator.Suggest(new DateOnly(2026, 7, 10), 180m, rules).SuggestedPrice);
    }

    [Fact]
    public void Suggest_SeasonalityOff_IgnoresTheMonths()
    {
        var rules = Example with { IncludeSeasonality = false };

        Assert.Equal(SeasonalPriceRule.None, SeasonalPriceCalculator.Suggest(new DateOnly(2026, 7, 14), 180m, rules).Rule);
    }

    [Fact]
    public void Suggest_OddPrice_RoundsToTheCent()
    {
        var day = SeasonalPriceCalculator.Suggest(new DateOnly(2026, 7, 14), 99.99m, Example);

        Assert.Equal(129.99m, day.SuggestedPrice); // 129.987 -> 129.99
    }

    [Fact]
    public void Window_FromToday_HasNinetyConsecutiveDays()
    {
        var window = SeasonalPriceCalculator.Window(new DateOnly(2026, 9, 24), 180m, Example);

        Assert.Equal(SeasonalPriceCalculator.WindowDays, window.Count);
        Assert.Equal(new DateOnly(2026, 9, 24), window[0].Date);
        Assert.Equal(new DateOnly(2026, 12, 22), window[^1].Date);
        Assert.All(window, d => Assert.Equal(180m, d.BasePrice));
    }
}

public class SeasonalSuggestionScheduleTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    [Fact]
    public void IsDue_NeverComputed_IsDue()
    {
        Assert.True(SeasonalSuggestionSchedule.IsDue("weekly", null, Today));
        Assert.Null(SeasonalSuggestionSchedule.NextRunOn("weekly", null));
    }

    [Fact]
    public void IsDue_DailyRunEarlierInTheDayThanYesterday_IsDue()
    {
        // Yesterday's run at 02:00:05Z, today's job fires at 01:59:58Z: by instants less than a day, by dates due.
        var last = new DateTime(2026, 9, 23, 2, 0, 5, DateTimeKind.Utc);

        Assert.True(SeasonalSuggestionSchedule.IsDue("daily", last, Today));
    }

    [Fact]
    public void IsDue_DailyAlreadyComputedToday_IsNotDue()
    {
        var last = new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Utc);

        Assert.False(SeasonalSuggestionSchedule.IsDue("daily", last, Today));
    }

    [Theory]
    [InlineData(18, false)]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    public void IsDue_WeeklyComputedOnThe17th_IsDueFromSevenDaysLater(int day, bool expected)
    {
        var last = new DateTime(2026, 9, 17, 2, 3, 0, DateTimeKind.Utc);

        Assert.Equal(expected, SeasonalSuggestionSchedule.IsDue("weekly", last, new DateOnly(2026, 9, day)));
        Assert.Equal(new DateOnly(2026, 9, 24), SeasonalSuggestionSchedule.NextRunOn("weekly", last));
    }

    [Fact]
    public void NextRunOn_LateEveningUtc_UsesTheRomeDate()
    {
        // 23:30Z on the 23rd is already the 24th in Rome (CEST).
        var last = new DateTime(2026, 9, 23, 23, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 9, 25), SeasonalSuggestionSchedule.NextRunOn("daily", last));
    }

    [Theory]
    [InlineData("daily", 1)]
    [InlineData("weekly", 7)]
    [InlineData("WEEKLY", 7)]
    [InlineData("", 1)]
    public void IntervalDays_Frequency_IsOneOrSeven(string frequency, int expected)
    {
        Assert.Equal(expected, SeasonalSuggestionSchedule.IntervalDays(frequency));
    }
}
