using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US01/T02 (NF-36): the pure rules of the price and the rules of a night: the patch, the bounds, the rounding of the price, the clock
/// of a property, where the price of a night comes from, and the sentences of the refusals. No database.
/// </summary>
public class DayRulesTests
{
    // --- The patch ---------------------------------------------------------------------------------------------

    [Fact]
    public void FieldPatch_ByDefaultKeepsTheValue_AndSetAndClearSayWhatTheyDo()
    {
        Assert.Equal(FieldPatchKind.Keep, default(FieldPatch<int>).Kind);
        Assert.Equal(FieldPatchKind.Keep, FieldPatch<int>.Keep.Kind);
        Assert.Equal(7, FieldPatch<int>.Keep.ApplyTo(7));
        Assert.Null(FieldPatch<int>.Keep.ApplyTo(null));
        Assert.Equal(3, FieldPatch<int>.Set(3).ApplyTo(7));
        Assert.Equal(3, FieldPatch<int>.Set(3).ApplyTo(null));
        Assert.Null(FieldPatch<int>.Clear.ApplyTo(7));
    }

    [Fact]
    public void DayRulePatch_ANightEndsUpWithTheValuesOfThePatchOverWhatItHad()
    {
        var current = new DayRuleValues(Price: 150m, MinNights: 2, MaxNights: 6, ClosedToArrival: true, MinAdvanceDays: 1);
        var patch = new DayRulePatch(
            Price: FieldPatch<decimal>.Clear,
            MinNights: FieldPatch<int>.Set(3),
            ClosedToArrival: FieldPatch<bool>.Clear,
            ClosedToDeparture: FieldPatch<bool>.Set(true),
            MaxAdvanceDays: FieldPatch<int>.Set(60));

        var after = patch.ApplyTo(current);

        // What the patch names changes; what it does not name stays (the maximum stay and the minimum advance).
        Assert.Equal(new DayRuleValues(null, 3, 6, false, true, 1, 60), after);
        Assert.Equal(current, new DayRulePatch().ApplyTo(current));
        Assert.True(new DayRulePatch().IsEmpty);
        Assert.False(patch.IsEmpty);
    }

    [Theory]
    [InlineData("120.505", "120.51")]
    [InlineData("120.504", "120.50")]
    [InlineData("0.005", "0.01")]
    [InlineData("99.995", "100.00")]
    public void DayRulePatch_ThePriceIsRoundedToTheCentHalfAwayFromZero_AsTheStayPricingRoundsAnAmount(string given, string stored)
    {
        var after = new DayRulePatch(Price: FieldPatch<decimal>.Set(decimal.Parse(given, System.Globalization.CultureInfo.InvariantCulture)))
            .ApplyTo(DayRuleValues.None);

        Assert.Equal(decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture), after.Price);
    }

    [Fact]
    public void DayRuleValues_ARowExistsOnlyWhileAValueIsSet()
    {
        Assert.False(DayRuleValues.None.HasAny);
        Assert.False(new DayRuleValues(ClosedToArrival: false).HasAny);
        Assert.True(new DayRuleValues(ClosedToArrival: true).HasAny);
        Assert.True(new DayRuleValues(Price: 1m).HasAny);
        Assert.True(new DayRuleValues(MaxAdvanceDays: 0).HasAny);
    }

    [Fact]
    public void DayRuleValues_AreTheValuesOfTheRow_AndWriteBackIntoIt()
    {
        var row = new PropertyDayRule { Price = 99.9m, MinNights = 2, MaxNights = 4, ClosedToDeparture = true, MinAdvanceDays = 3, MaxAdvanceDays = 9 };
        var values = DayRuleValues.Of(row);
        var other = new PropertyDayRule();
        values.WriteTo(other);

        Assert.Equal(new DayRuleValues(99.9m, 2, 4, false, true, 3, 9), values);
        Assert.Equal((99.9m, 2, 4, false, true, 3, 9), (other.Price, other.MinNights, other.MaxNights, other.ClosedToArrival, other.ClosedToDeparture, other.MinAdvanceDays, other.MaxAdvanceDays));
    }

    // --- The bounds --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.01", null)]
    [InlineData("100000", null)]
    [InlineData("0", "calendar_day_rule_invalid_price")]
    [InlineData("0.004", "calendar_day_rule_invalid_price")]
    [InlineData("-3", "calendar_day_rule_invalid_price")]
    [InlineData("100000.01", "calendar_day_rule_invalid_price")]
    public void Validate_ThePriceIsBetweenOneCentAndOneHundredThousand(string price, string? expected)
    {
        var values = new DayRuleValues(Price: decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected, DayRuleRules.Validate(values));
    }

    [Theory]
    [InlineData(1, 365, null)]
    [InlineData(3, 3, null)]
    [InlineData(0, null, "calendar_day_rule_invalid_stay")]
    [InlineData(null, 0, "calendar_day_rule_invalid_stay")]
    [InlineData(null, 366, "calendar_day_rule_invalid_stay")]
    [InlineData(366, null, "calendar_day_rule_invalid_stay")]
    [InlineData(4, 3, "calendar_day_rule_invalid_stay")]
    [InlineData(null, 1, null)]
    public void Validate_AStayIsFromOneToThreeHundredSixtyFiveNights_AndTheMaximumIsNotBelowTheMinimum(int? min, int? max, string? expected)
    {
        Assert.Equal(expected, DayRuleRules.Validate(new DayRuleValues(MinNights: min, MaxNights: max)));
    }

    [Theory]
    [InlineData(0, 730, null)]
    [InlineData(5, 5, null)]
    [InlineData(-1, null, "calendar_day_rule_invalid_advance")]
    [InlineData(null, 731, "calendar_day_rule_invalid_advance")]
    [InlineData(10, 9, "calendar_day_rule_invalid_advance")]
    public void Validate_AnAdvanceIsFromZeroToSevenHundredThirtyDays_AndTheMaximumIsNotBelowTheMinimum(int? min, int? max, string? expected)
    {
        Assert.Equal(expected, DayRuleRules.Validate(new DayRuleValues(MinAdvanceDays: min, MaxAdvanceDays: max)));
    }

    [Fact]
    public void Validate_TheFirstProblemWins_ThePriceBeforeTheStayBeforeTheAdvance()
    {
        var everythingWrong = new DayRuleValues(Price: 0m, MinNights: 5, MaxNights: 2, MinAdvanceDays: 9, MaxAdvanceDays: 1);

        Assert.Equal(DayRuleErrorCodes.InvalidPrice, DayRuleRules.Validate(everythingWrong));
        Assert.Equal(DayRuleErrorCodes.InvalidStay, DayRuleRules.Validate(everythingWrong with { Price = 10m }));
        Assert.Equal(DayRuleErrorCodes.InvalidAdvance, DayRuleRules.Validate(everythingWrong with { Price = 10m, MinNights = 1, MaxNights = 2 }));
        Assert.Null(DayRuleRules.Validate(DayRuleValues.None));
    }

    [Fact]
    public void Limits_AreTheOnesTheDatabaseChecks()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var check = Assert.Single(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(PropertyDayRule))!.GetCheckConstraints());

        Assert.Equal(PropertyDayRule.ValuesCheckName, check.Name);
        Assert.Contains($"\"Price\" >= {DayRuleLimits.MinPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}", check.Sql, StringComparison.Ordinal);
        Assert.Contains($"\"Price\" <= {DayRuleLimits.MaxPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}", check.Sql, StringComparison.Ordinal);
        Assert.Contains($"\"MinNights\" >= {DayRuleLimits.MinStayNights} AND \"MinNights\" <= {DayRuleLimits.MaxStayNights}", check.Sql, StringComparison.Ordinal);
        Assert.Contains($"\"MaxAdvanceDays\" >= {DayRuleLimits.MinAdvance} AND \"MaxAdvanceDays\" <= {DayRuleLimits.MaxAdvance}", check.Sql, StringComparison.Ordinal);
        Assert.Equal((0.01m, 100000m, 1, 365, 0, 730, 366), (
            DayRuleLimits.MinPrice, DayRuleLimits.MaxPrice, DayRuleLimits.MinStayNights, DayRuleLimits.MaxStayNights, DayRuleLimits.MinAdvance,
            DayRuleLimits.MaxAdvance, DayRuleLimits.MaxRangeDays));
    }

    // --- The range of a change ---------------------------------------------------------------------------------

    [Fact]
    public void EnsureRange_AYearAndADayOfALeapYearIsTheLongest_AndTheLastDayIsNotBeforeTheFirst()
    {
        var first = new DateOnly(2028, 1, 1);

        DayRuleRules.EnsureRange(first, first);
        DayRuleRules.EnsureRange(first, first.AddDays(DayRuleLimits.MaxRangeDays - 1));
        var tooLong = Assert.Throws<DomainRuleException>(() => DayRuleRules.EnsureRange(first, first.AddDays(DayRuleLimits.MaxRangeDays)));
        var backwards = Assert.Throws<DomainRuleException>(() => DayRuleRules.EnsureRange(first, first.AddDays(-1)));

        Assert.Equal(("calendar_day_rule_range_too_long", "CalendarDayRuleRangeTooLong"), (tooLong.Code, tooLong.MessageKey));
        Assert.Equal(366, Assert.Single(tooLong.MessageArgs));
        Assert.Equal(("calendar_day_rule_range_invalid", "CalendarDayRuleRangeInvalid"), (backwards.Code, backwards.MessageKey));
    }

    // --- The clock of the property -----------------------------------------------------------------------------

    [Theory]
    [InlineData("Europe/Rome", "2026-03-28T22:30:00Z", "2026-03-28")]
    [InlineData("Europe/Rome", "2026-03-28T23:30:00Z", "2026-03-29")]
    [InlineData("Pacific/Auckland", "2026-03-28T12:00:00Z", "2026-03-29")]
    [InlineData("America/Los_Angeles", "2026-03-29T05:00:00Z", "2026-03-28")]
    [InlineData("Europe/Zurich", "2026-10-24T22:30:00Z", "2026-10-25")]
    public void TodayOf_IsTheDateOnTheClockOfTheProperty(string zone, string instant, string expected)
    {
        var utcNow = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DayRuleRules.TodayOf(zone, utcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/AZone")]
    public void TodayOf_AMissingOrUnknownZone_IsTheCalendarOfTheMarket(string? zone)
    {
        var utcNow = new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 3, 29), DayRuleRules.TodayOf(zone, utcNow));
    }

    // --- Where the price comes from ----------------------------------------------------------------------------

    [Fact]
    public void HostPrices_TheDayPriceOverridesTheSeasonalOne_AndANightWithoutADayPriceKeepsTheSeasonalOne()
    {
        var friday = new DateOnly(2026, 12, 25);
        var seasonal = new Dictionary<DateOnly, decimal> { [friday] = 130m, [friday.AddDays(1)] = 140m };
        var rules = new Dictionary<DateOnly, DayRuleValues>
        {
            [friday] = new(Price: 160m),
            [friday.AddDays(1)] = new(MinNights: 3),
            [friday.AddDays(2)] = new(Price: 90m),
        };

        var prices = DayRulePricing.HostPrices(seasonal, rules);

        Assert.Equal(160m, prices[friday]);
        Assert.Equal(140m, prices[friday.AddDays(1)]);
        Assert.Equal(90m, prices[friday.AddDays(2)]);
        Assert.Equal(3, prices.Count);
        // The inputs are left as they were, and nothing at all gives nothing.
        Assert.Equal(130m, seasonal[friday]);
        Assert.Empty(DayRulePricing.HostPrices(null, null));
    }

    [Fact]
    public void SourceOf_TheDayRuleThenTheSeasonalPriceThenTheWeekendThenTheRate()
    {
        var friday = new DateOnly(2026, 12, 25);
        var seasonal = new Dictionary<DateOnly, decimal> { [friday] = 130m };

        Assert.Equal(DayPriceSource.DayRule, DayRulePricing.SourceOf(friday, new DayRuleValues(Price: 150m), seasonal, 20m));
        Assert.Equal(DayPriceSource.Seasonal, DayRulePricing.SourceOf(friday, new DayRuleValues(MinNights: 3), seasonal, 20m));
        Assert.Equal(DayPriceSource.Seasonal, DayRulePricing.SourceOf(friday, null, seasonal, 20m));
        Assert.Equal(DayPriceSource.Weekend, DayRulePricing.SourceOf(friday, null, null, 20m));
        Assert.Equal(DayPriceSource.Weekend, DayRulePricing.SourceOf(friday.AddDays(1), null, null, 20m));
        Assert.Equal(DayPriceSource.Base, DayRulePricing.SourceOf(friday.AddDays(2), null, null, 20m));
        Assert.Equal(DayPriceSource.Base, DayRulePricing.SourceOf(friday, null, null, 0m));
    }

    [Fact]
    public void SourceOf_AgreesWithThePriceOfTheQuote_ForEachSource()
    {
        // The source says which number the quote takes: the day price, the seasonal price, the rate with the surcharge, the rate.
        var friday = new DateOnly(2026, 12, 25);
        var seasonal = new Dictionary<DateOnly, decimal> { [friday.AddDays(3)] = 130m };
        var rules = new Dictionary<DateOnly, DayRuleValues> { [friday] = new(Price: 150m) };
        var prices = DayRulePricing.HostPrices(seasonal, rules);

        Assert.Equal(150m, StayPricing.NightPrice(friday, 100m, 20m, prices));
        Assert.Equal(120m, StayPricing.NightPrice(friday.AddDays(1), 100m, 20m, prices));
        Assert.Equal(100m, StayPricing.NightPrice(friday.AddDays(2), 100m, 20m, prices));
        Assert.Equal(130m, StayPricing.NightPrice(friday.AddDays(3), 100m, 20m, prices));
    }

    [Fact]
    public void MinNightsOf_TheRuleThenTheMinimumOfThePropertyThenOne()
    {
        Assert.Equal(4, DayRuleDefaults.MinNightsOf(new DayRuleValues(MinNights: 4), 2));
        Assert.Equal(1, DayRuleDefaults.MinNightsOf(new DayRuleValues(MinNights: 1), 2));
        Assert.Equal(2, DayRuleDefaults.MinNightsOf(new DayRuleValues(Price: 99m), 2));
        Assert.Equal(2, DayRuleDefaults.MinNightsOf(null, 2));
        Assert.Equal(1, DayRuleDefaults.MinNightsOf(null, null));
    }

    // --- The sentences -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DayRuleErrorCodes.InvalidPrice, "calendar_day_rule_invalid_price", "CalendarDayRuleInvalidPrice")]
    [InlineData(DayRuleErrorCodes.InvalidStay, "calendar_day_rule_invalid_stay", "CalendarDayRuleInvalidStay")]
    [InlineData(DayRuleErrorCodes.InvalidAdvance, "calendar_day_rule_invalid_advance", "CalendarDayRuleInvalidAdvance")]
    [InlineData(DayRuleErrorCodes.RangeInvalid, "calendar_day_rule_range_invalid", "CalendarDayRuleRangeInvalid")]
    [InlineData(DayRuleErrorCodes.RangeTooLong, "calendar_day_rule_range_too_long", "CalendarDayRuleRangeTooLong")]
    [InlineData(DayRuleErrorCodes.InPast, "calendar_day_rule_in_past", "CalendarDayRuleInPast")]
    [InlineData(DayRuleErrorCodes.NothingToChange, "calendar_day_rule_nothing_to_change", "CalendarDayRuleNothingToChange")]
    public void ErrorCodes_AreStable_AndEachHasItsSentenceInItalianAndEnglish(string code, string expectedCode, string expectedKey)
    {
        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedKey, DayRuleErrorCodes.MessageKeyOf(code));
        foreach (var file in new[] { "SharedResources.resx", "SharedResources.en.resx" })
        {
            var text = ReadResource(file, expectedKey);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{file} has no sentence for {expectedKey}");
        }
    }

    [Fact]
    public void ProblemOf_ThePriceStayAndAdvanceSentencesNameTheirBounds_AndTheOthersNameNothing()
    {
        Assert.Equal(new object[] { 0.01m, 100000m }, DayRuleRules.ProblemOf(DayRuleErrorCodes.InvalidPrice).MessageArgs);
        Assert.Equal(new object[] { 1, 365 }, DayRuleRules.ProblemOf(DayRuleErrorCodes.InvalidStay).MessageArgs);
        Assert.Equal(new object[] { 0, 730 }, DayRuleRules.ProblemOf(DayRuleErrorCodes.InvalidAdvance).MessageArgs);
        Assert.Empty(DayRuleRules.ProblemOf(DayRuleErrorCodes.NothingToChange).MessageArgs);
        Assert.Equal("some_other_code", DayRuleRules.ProblemOf("some_other_code").MessageKey);
    }

    /// <summary>The text of a resource key in one of the resource files of the API, or <c>null</c> when the file has none.</summary>
    internal static string? ReadResource(string file, string key)
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        var path = Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder."),
            "Casazen.Web",
            "Resources",
            file);
        return XDocument.Load(path).Root!
            .Elements("data")
            .FirstOrDefault(e => (string?)e.Attribute("name") == key)?
            .Element("value")?.Value;
    }
}
