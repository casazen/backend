using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

public class OctorateCalendarEntriesTests
{
    private static readonly OctorateBookingTarget Target = new("ACC1", "956528");

    private static readonly DateOnly First = new(2026, 12, 1);

    private static readonly IReadOnlySet<DateOnly> None = new HashSet<DateOnly>();

    private static readonly IReadOnlyDictionary<DateOnly, DayRuleValues> NoRules = new Dictionary<DateOnly, DayRuleValues>();

    private static readonly IReadOnlyDictionary<DateOnly, decimal> NoPrices = new Dictionary<DateOnly, decimal>();

    /// <summary>
    /// PROOF TEST: money rule. With a weekend surcharge, a seasonal price the host confirmed and a price of a day rule in the window, the price of every night in the entries is
    /// <see cref="StayPricing.NightPrice"/> of that night, to the cent, and the nights of a stay add up to the lodging of its quote. A day rule whose price was cleared sends the
    /// usual price again, never the old one.
    /// </summary>
    [Fact]
    public void OctorateCalendarEntries_PriceEqualsQuoteNightPrice_ToTheCent_Test()
    {
        var property = new OctorateCalendarProperty(99.99m, 15m, null);
        var last = First.AddDays(13);
        var seasonal = new Dictionary<DateOnly, decimal> { [First.AddDays(4)] = 140.40m };
        var rules = new Dictionary<DateOnly, DayRuleValues>
        {
            [First.AddDays(7)] = new(Price: 77.77m, MinNights: 2),
            [First.AddDays(8)] = new(MinNights: 3),
        };
        var hostPrices = DayRulePricing.HostPrices(seasonal, rules);

        var entries = OctorateCalendarEntries.Build(property, Target, First, last, rules, hostPrices, None, None, new OctorateCalendarOptions());

        var sum = 0m;
        for (var night = First; night <= last; night = night.AddDays(1))
        {
            var quoted = StayPricing.NightPrice(night, 99.99m, 15m, hostPrices);
            var entry = entries.Single(e => e.From <= night && night <= e.To);
            Assert.Equal(quoted, entry.Values.Price);
            Assert.Equal(StayPricing.ToCents(quoted), StayPricing.ToCents(entry.Values.Price!.Value));
            sum += entry.Values.Price!.Value;
        }

        Assert.Equal(StayPricing.Lodging(First, 14, 99.99m, 15m, hostPrices).Total, sum);
        Assert.Equal(114.99m, entries.Single(e => e.From <= First.AddDays(3) && First.AddDays(3) <= e.To).Values.Price);
        Assert.Equal(140.40m, entries.Single(e => e.From <= First.AddDays(4) && First.AddDays(4) <= e.To).Values.Price);
        Assert.Equal(77.77m, entries.Single(e => e.From <= First.AddDays(7) && First.AddDays(7) <= e.To).Values.Price);

        var cleared = new Dictionary<DateOnly, DayRuleValues> { [First.AddDays(7)] = new(MinNights: 2), [First.AddDays(8)] = new(MinNights: 3) };
        var clearedPrices = DayRulePricing.HostPrices(seasonal, cleared);
        var again = OctorateCalendarEntries.Build(property, Target, First, last, cleared, clearedPrices, None, None, new OctorateCalendarOptions());
        var night7 = again.Single(e => e.From <= First.AddDays(7) && First.AddDays(7) <= e.To);
        Assert.Equal(StayPricing.NightPrice(First.AddDays(7), 99.99m, 15m, clearedPrices), night7.Values.Price);
        Assert.Equal(99.99m, night7.Values.Price);
    }

    [Fact]
    public void Build_AdjacentNightsWithEqualValues_BecomeOneInterval_AndAChangeSplitsIt()
    {
        var property = new OctorateCalendarProperty(100m, 0m, null);

        var flat = OctorateCalendarEntries.Build(property, Target, First, First.AddDays(6), NoRules, NoPrices, None, None, new OctorateCalendarOptions());
        var interval = Assert.Single(flat);
        Assert.Equal((Target.ProductId, First, First.AddDays(6)), (interval.RoomId, interval.From, interval.To));
        Assert.Equal(7, interval.Days);

        var rules = new Dictionary<DateOnly, DayRuleValues> { [First.AddDays(2)] = new(MinNights: 2) };
        var split = OctorateCalendarEntries.Build(property, Target, First, First.AddDays(6), rules, NoPrices, None, None, new OctorateCalendarOptions());
        Assert.Equal(
            [(First, First.AddDays(1)), (First.AddDays(2), First.AddDays(2)), (First.AddDays(3), First.AddDays(6))],
            split.Select(e => (e.From, e.To)));
        Assert.Equal([1, 2, 1], split.Select(e => e.Values.MinStay));
    }

    [Fact]
    public void Build_SendsTheStayAndAdvanceRulesOfEveryNight_AndNeverTheMaximumAdvance()
    {
        var property = new OctorateCalendarProperty(100m, 0m, 2);
        var rule = new DayRuleValues(Price: 120m, MinNights: 3, MaxNights: 14, ClosedToArrival: true, ClosedToDeparture: true, MinAdvanceDays: 2, MaxAdvanceDays: 30);
        var rules = new Dictionary<DateOnly, DayRuleValues> { [First] = rule };
        var prices = DayRulePricing.HostPrices(null, rules);

        var entry = Assert.Single(OctorateCalendarEntries.Build(property, Target, First, First, rules, prices, None, None, new OctorateCalendarOptions()));

        Assert.Equal(120m, entry.Values.Price);
        Assert.Equal(3, entry.Values.MinStay);
        Assert.Equal(14, entry.Values.MaxStay);
        Assert.True(entry.Values.CloseToArrival);
        Assert.True(entry.Values.CloseToDeparture);
        Assert.Equal(2, entry.Values.CutOffDays);

        var withoutMaxAdvance = new Dictionary<DateOnly, DayRuleValues> { [First] = rule with { MaxAdvanceDays = null } };
        var same = Assert.Single(OctorateCalendarEntries.Build(property, Target, First, First, withoutMaxAdvance, prices, None, None, new OctorateCalendarOptions()));
        Assert.Equal(entry.Values, same.Values);

        var plain = Assert.Single(OctorateCalendarEntries.Build(property, Target, First, First, NoRules, NoPrices, None, None, new OctorateCalendarOptions()));
        Assert.Equal(2, plain.Values.MinStay);
        Assert.Null(plain.Values.MaxStay);
        Assert.False(plain.Values.CloseToArrival);
        Assert.False(plain.Values.CloseToDeparture);
        Assert.Equal(0, plain.Values.CutOffDays);

        var noMinimum = new OctorateCalendarProperty(100m, 0m, null);
        var one = Assert.Single(OctorateCalendarEntries.Build(noMinimum, Target, First, First, NoRules, NoPrices, None, None, new OctorateCalendarOptions { NoMaxStay = 999 }));
        Assert.Equal(1, one.Values.MinStay);
        Assert.Equal(999, one.Values.MaxStay);
    }

    [Fact]
    public void Build_ClosesTheNightsOfAManualBlock_WithStopSellsOrWithAvailability_AndLeavesAloneWhatSomethingElseHolds()
    {
        var property = new OctorateCalendarProperty(100m, 0m, null);
        var closed = new HashSet<DateOnly> { First.AddDays(2), First.AddDays(3) };
        var held = new HashSet<DateOnly> { First.AddDays(5) };

        var byStop = OctorateCalendarEntries.Build(property, Target, First, First.AddDays(6), NoRules, NoPrices, closed, held, new OctorateCalendarOptions());

        Assert.Equal(
            [false, false, true, true, false, null, false],
            Enumerable.Range(0, 7).Select(i => byStop.Single(e => e.From <= First.AddDays(i) && First.AddDays(i) <= e.To).Values.StopSells));
        Assert.All(byStop, e => Assert.Null(e.Values.Availability));

        var byUnits = OctorateCalendarEntries.Build(
            property, Target, First, First.AddDays(6), NoRules, NoPrices, closed, held, new OctorateCalendarOptions { CloseNightsWith = OctorateCloseNightsWith.Availability });
        Assert.Equal(
            [1, 1, 0, 0, 1, null, 1],
            Enumerable.Range(0, 7).Select(i => byUnits.Single(e => e.From <= First.AddDays(i) && First.AddDays(i) <= e.To).Values.Availability));
        Assert.All(byUnits, e => Assert.Null(e.Values.StopSells));

        var both = OctorateCalendarEntries.Build(
            property, Target, First, First, NoRules, NoPrices, new HashSet<DateOnly> { First }, new HashSet<DateOnly> { First }, new OctorateCalendarOptions());
        Assert.True(Assert.Single(both).Values.StopSells);
    }

    [Fact]
    public void Build_ANightWithNoPriceAtAll_SendsNoPrice_NeverZero_AndTheNightsTheHostPricedKeepTheirs()
    {
        var property = new OctorateCalendarProperty(0m, 0m, null);
        var rules = new Dictionary<DateOnly, DayRuleValues> { [First.AddDays(2)] = new(Price: 90m) };
        var prices = DayRulePricing.HostPrices(null, rules);

        var entries = OctorateCalendarEntries.Build(property, Target, First, First.AddDays(4), rules, prices, None, None, new OctorateCalendarOptions());

        Assert.Equal(
            new decimal?[] { null, null, 90m, null, null },
            Enumerable.Range(0, 5).Select(i => entries.Single(e => e.From <= First.AddDays(i) && First.AddDays(i) <= e.To).Values.Price));
        Assert.DoesNotContain(entries, e => e.Values.Price == 0m);

        var block = OctorateCalendarEntries.Build(
            property, Target, First, First.AddDays(1), NoRules, NoPrices, new HashSet<DateOnly> { First }, None, new OctorateCalendarOptions());
        Assert.Equal(new bool?[] { true, false }, block.Select(e => e.Values.StopSells));
        Assert.All(block, e => Assert.Null(e.Values.Price));
    }

    [Fact]
    public void Build_AnEmptyWindow_SendsNothing()
    {
        var property = new OctorateCalendarProperty(100m, 0m, null);

        Assert.Empty(OctorateCalendarEntries.Build(property, Target, First.AddDays(1), First, NoRules, NoPrices, None, None, new OctorateCalendarOptions()));
    }

    [Fact]
    public void Split_AYearOfDays_IsCutIntoCallsOfAtMostTheConfiguredDays_WithoutGapOrOverlap()
    {
        var last = First.AddDays(364);

        var chunks = CalendarChunkPlanner.Split(First, last, 62);

        Assert.Equal(6, chunks.Count);
        Assert.Equal(First, chunks[0].From);
        Assert.Equal(last, chunks[^1].To);
        Assert.All(chunks, c => Assert.InRange(c.Days, 1, 62));
        for (var i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].To.AddDays(1), chunks[i].From);

        Assert.Equal(365, chunks.Sum(c => c.Days));
        Assert.Equal(55, chunks[^1].Days);
        Assert.Single(CalendarChunkPlanner.Split(First, First.AddDays(61), 62));
        Assert.Empty(CalendarChunkPlanner.Split(First.AddDays(1), First, 62));
        Assert.Equal(3, CalendarChunkPlanner.Split(First, First.AddDays(2), 0).Count);
    }

    [Theory]
    [InlineData(0, 15, 0)]
    [InlineData(1, 15, 1)]
    [InlineData(15, 15, 1)]
    [InlineData(16, 15, 2)]
    [InlineData(62, 15, 5)]
    [InlineData(30, 0, 30)]
    public void EstimateQuotaUnits_IsOneUnitForEveryRoomDaysPerUnit_TheLastOneStartedIncluded(int roomDays, int perUnit, int expected)
    {
        Assert.Equal(expected, CalendarChunkPlanner.EstimateQuotaUnits(roomDays, perUnit));
    }

    [Fact]
    public void EstimateQuotaUnits_OfAWindow_IsTheSumOfItsCalls()
    {
        Assert.Equal(5 * 5 + 4, CalendarChunkPlanner.EstimateQuotaUnits(First, First.AddDays(364), 62, 15));
        Assert.Equal(0, CalendarChunkPlanner.EstimateQuotaUnits(First.AddDays(1), First, 62, 15));
    }

    [Fact]
    public void TheOptions_BringAValueOutOfRangeBackIntoIt_AndTheDefaultsAreTheDecidedOnes()
    {
        var defaults = new OctorateCalendarOptions();
        Assert.Equal((62, 15, 20, 365, OctorateCloseNightsWith.StopSells), (defaults.MaxDays, defaults.RoomDaysPerUnit, defaults.Reserve, defaults.Horizon, defaults.CloseNightsWith));
        Assert.Null(defaults.NoMaxStayNights);

        var wrong = new OctorateCalendarOptions { MaxDaysPerCall = 0, RoomDaysPerQuotaUnit = -3, QuotaReserve = -1, HorizonDays = 5000, NoMaxStay = 0 };
        Assert.Equal((62, 15, 20, 365), (wrong.MaxDays, wrong.RoomDaysPerUnit, wrong.Reserve, wrong.Horizon));
        Assert.Null(wrong.NoMaxStayNights);
        Assert.Equal(730, new OctorateCalendarOptions { HorizonDays = 730 }.Horizon);
    }

    [Theory]
    [InlineData(new CalendarChunkState[0], CalendarChangeState.Applied)]
    [InlineData(new[] { CalendarChunkState.Skipped }, CalendarChangeState.Applied)]
    [InlineData(new[] { CalendarChunkState.Pending, CalendarChunkState.Pending }, CalendarChangeState.Queued)]
    [InlineData(new[] { CalendarChunkState.Pending, CalendarChunkState.Failed }, CalendarChangeState.Queued)]
    [InlineData(new[] { CalendarChunkState.Accepted, CalendarChunkState.Pending }, CalendarChangeState.Sending)]
    [InlineData(new[] { CalendarChunkState.Accepted, CalendarChunkState.Accepted }, CalendarChangeState.Sent)]
    [InlineData(new[] { CalendarChunkState.Accepted, CalendarChunkState.Confirmed }, CalendarChangeState.Sent)]
    [InlineData(new[] { CalendarChunkState.Confirmed, CalendarChunkState.Confirmed, CalendarChunkState.Skipped }, CalendarChangeState.Confirmed)]
    [InlineData(new[] { CalendarChunkState.Accepted, CalendarChunkState.Failed }, CalendarChangeState.PartiallySent)]
    [InlineData(new[] { CalendarChunkState.Failed, CalendarChunkState.Failed }, CalendarChangeState.Failed)]
    public void Derive_TheStateOfAProcessFollowsItsChunks_AndNeverSaysPublished(CalendarChunkState[] chunks, CalendarChangeState expected)
    {
        Assert.Equal(expected, CalendarPushRules.Derive(chunks));
        Assert.DoesNotContain("Publish", Enum.GetNames<CalendarChangeState>(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backoff_IsOneTwoFourThenEightMinutes_AndTheFifthAttemptIsTheLast()
    {
        Assert.Equal([1, 2, 4, 8, 8], Enumerable.Range(1, 5).Select(attempt => (int)CalendarPushRules.BackoffAfter(attempt).TotalMinutes));
        Assert.Equal(5, CalendarPushRules.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), CalendarPushRules.QuotaRetryAfter);
    }

    [Fact]
    public void ProcessIds_AreKeptInTheColumnOfTheChunk_AndAListThatDoesNotFitIsNeverReadAsWhole()
    {
        Assert.Null(CalendarPushRules.JoinProcessIds([]));
        Assert.Equal("123,456", CalendarPushRules.JoinProcessIds(["123", " 456 ", "123", ""]));
        var (whole, wholeComplete) = CalendarPushRules.ParseProcessIds("123,456");
        Assert.Equal(["123", "456"], whole);
        Assert.True(wholeComplete);
        var (none, noneComplete) = CalendarPushRules.ParseProcessIds(null);
        Assert.Empty(none);
        Assert.True(noneComplete);

        var many = Enumerable.Range(0, 40).Select(i => (100_000_000 + i).ToString()).ToList();
        var stored = CalendarPushRules.JoinProcessIds(many)!;
        Assert.True(stored.Length <= CalendarChangeChunk.OctorateProcessIdMaxLength);
        Assert.EndsWith(CalendarPushRules.TruncatedMarker.ToString(), stored);
        var (ids, complete) = CalendarPushRules.ParseProcessIds(stored);
        Assert.False(complete);
        Assert.Equal(many.Take(ids.Count), ids);
        Assert.InRange(ids.Count, 1, 39);
    }

    private static readonly IReadOnlySet<string> Success = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SUCCESS_UNDER_TEST" };

    private static readonly IReadOnlySet<string> NoSuccess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("NEW", new[] { "NOT_PROCESSED" }, CalendarProcessVerdict.Pending)]
    [InlineData("SUCCESS_UNDER_TEST", new[] { "SUCCESS_UNDER_TEST", "NOT_PROCESSED" }, CalendarProcessVerdict.Pending)]
    [InlineData("SUCCESS_UNDER_TEST", new[] { "SUCCESS_UNDER_TEST", "ERROR" }, CalendarProcessVerdict.Error)]
    [InlineData("ERROR", new string[0], CalendarProcessVerdict.Error)]
    [InlineData("SUCCESS_UNDER_TEST", new string[0], CalendarProcessVerdict.Pending)]
    [InlineData("SUCCESS_UNDER_TEST", new[] { "SUCCESS_UNDER_TEST", "success_under_test" }, CalendarProcessVerdict.Confirmed)]
    [InlineData("PROCESSING", new[] { "SUCCESS_UNDER_TEST" }, CalendarProcessVerdict.Pending)]
    [InlineData("SUCCESS_UNDER_TEST", new[] { "SUCCESS_UNDER_TEST", "PROCESSING" }, CalendarProcessVerdict.Pending)]
    [InlineData("SUCCESS_UNDER_TEST", new[] { "SUCCESS_UNDER_TEST", "SOMETHING_UNKNOWN" }, CalendarProcessVerdict.Pending)]
    public void Verdict_ConfirmsOnlyWhatTheConfigurationNamesAsASuccess_OfTheProcessAndOfEveryDate(string status, string[] dates, CalendarProcessVerdict expected)
    {
        var process = new OctorateCalendarProcess("1", status, [.. dates.Select(s => new OctorateCalendarProcessDate(null, s))]);

        Assert.Equal(expected, CalendarPushRules.VerdictOf(process, Success));
    }

    [Fact]
    public void Verdict_AnAbsentStatus_NeverConfirms_AndWithNoSuccessStatusConfiguredNothingIsEverConfirmed()
    {
        var dated = new OctorateCalendarProcessDate(null, "SUCCESS_UNDER_TEST");
        var withoutStatus = new OctorateCalendarProcessDate(null, null);

        Assert.Equal(CalendarProcessVerdict.Pending, CalendarPushRules.VerdictOf(new OctorateCalendarProcess("1", null, [dated]), Success));
        Assert.Equal(CalendarProcessVerdict.Pending, CalendarPushRules.VerdictOf(new OctorateCalendarProcess("1", "SUCCESS_UNDER_TEST", [dated, withoutStatus]), Success));
        Assert.Equal(CalendarProcessVerdict.Pending, CalendarPushRules.VerdictOf(new OctorateCalendarProcess("1", "SUCCESS_UNDER_TEST", [withoutStatus]), Success));

        var done = new OctorateCalendarProcess("1", "SUCCESS_UNDER_TEST", [dated]);
        Assert.Equal(CalendarProcessVerdict.Pending, CalendarPushRules.VerdictOf(done, NoSuccess));
        Assert.Equal(CalendarProcessVerdict.Pending, CalendarPushRules.VerdictOf(done, new OctorateCalendarOptions().SuccessStatuses));
        Assert.Equal(CalendarProcessVerdict.Error, CalendarPushRules.VerdictOf(new OctorateCalendarProcess("1", "NEW", [new OctorateCalendarProcessDate(null, "ERROR")]), NoSuccess));
    }

    [Fact]
    public void TheSuccessStatuses_AreTheCommaSeparatedListOfTheConfiguration_TrimmedAndWithoutRegardToCase()
    {
        var options = new OctorateCalendarOptions { ProcessSuccessStatuses = " Done , OK;; " };

        Assert.Equal(["Done", "OK"], options.SuccessStatuses.Order().ToList());
        Assert.Contains("DONE", options.SuccessStatuses);
        Assert.Empty(new OctorateCalendarOptions { ProcessSuccessStatuses = "  " }.SuccessStatuses);
    }
}
