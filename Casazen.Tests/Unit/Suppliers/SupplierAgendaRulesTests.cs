using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class SupplierAgendaRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly DateTime Now = Utc("2026-10-08T10:00:00Z");

    [Fact]
    public void NormalizeHours_TheWeekOfTheDemo_IsReturnedMondayFirstThenByStart()
    {
        var input = Hours(
            Day(DayOfWeek.Sunday),
            Day(DayOfWeek.Saturday, Band(8 * 60, 14 * 60)),
            Day(DayOfWeek.Monday, Band(14 * 60, 18 * 60), Band(8 * 60, 13 * 60)));

        var bands = SupplierAgendaRules.NormalizeHours(input);

        Assert.Equal(
            [
                new SupplierWeeklyBand(DayOfWeek.Monday, 8 * 60, 13 * 60),
                new SupplierWeeklyBand(DayOfWeek.Monday, 14 * 60, 18 * 60),
                new SupplierWeeklyBand(DayOfWeek.Saturday, 8 * 60, 14 * 60),
            ],
            bands);
    }

    [Fact]
    public void NormalizeHours_AnEmptyListOfDays_IsAValidWeekWithoutHours()
    {
        Assert.Empty(SupplierAgendaRules.NormalizeHours(new SupplierHoursInput([])));
    }

    [Fact]
    public void NormalizeHours_ADayWithNoBandsOrANullList_IsARestDay()
    {
        var input = Hours(Day(DayOfWeek.Monday), new SupplierHoursDayInput(DayOfWeek.Tuesday, null));

        Assert.Empty(SupplierAgendaRules.NormalizeHours(input));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 1440)]
    [InlineData(1439, 1440)]
    public void NormalizeHours_TheEdgesOfTheDay_AreAccepted(int start, int end)
    {
        var bands = SupplierAgendaRules.NormalizeHours(Hours(Day(DayOfWeek.Monday, Band(start, end))));

        Assert.Equal(new SupplierWeeklyBand(DayOfWeek.Monday, start, end), Assert.Single(bands));
    }

    [Fact]
    public void NormalizeHours_BandsThatOnlyTouch_AreAccepted()
    {
        var bands = SupplierAgendaRules.NormalizeHours(
            Hours(Day(DayOfWeek.Monday, Band(8 * 60, 13 * 60), Band(13 * 60, 18 * 60))));

        Assert.Equal(2, bands.Count);
    }

    [Fact]
    public void NormalizeHours_ThreeBandsInADay_AreTheMostAllowed()
    {
        var bands = SupplierAgendaRules.NormalizeHours(
            Hours(Day(DayOfWeek.Monday, Band(6 * 60, 8 * 60), Band(9 * 60, 12 * 60), Band(14 * 60, 18 * 60))));

        Assert.Equal(3, bands.Count);
    }

    [Fact]
    public void NormalizeHours_MoreThanThreeBandsInADay_IsRefusedNamingTheBands()
    {
        var input = Hours(Day(
            DayOfWeek.Monday, Band(6 * 60, 7 * 60), Band(8 * 60, 9 * 60), Band(10 * 60, 11 * 60), Band(12 * 60, 13 * 60)));

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(input));

        Assert.Equal(SupplierAgendaErrors.HoursInvalid, ex.Code);
        Assert.Equal("SupplierHoursInvalid", ex.MessageKey);
        Assert.Equal(["days[0].bands"], ex.Fields);
    }

    [Theory]
    [InlineData(600, 600)]
    [InlineData(600, 540)]
    public void NormalizeHours_AnEndThatIsNotAfterItsStart_IsRefused(int start, int end)
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeHours(Hours(Day(DayOfWeek.Monday, Band(start, end)))));

        Assert.Equal(["days[0].bands[0].endMinute"], ex.Fields);
    }

    [Theory]
    [InlineData(-1, 600, "startMinute")]
    [InlineData(1440, 1440, "startMinute")]
    [InlineData(null, 600, "startMinute")]
    [InlineData(480, 0, "endMinute")]
    [InlineData(480, 1441, "endMinute")]
    [InlineData(480, null, "endMinute")]
    public void NormalizeHours_AMinuteOutsideItsRangeOrMissing_IsRefusedNamingTheMinute(int? start, int? end, string field)
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeHours(Hours(Day(DayOfWeek.Tuesday, new SupplierHoursBandInput(start, end)))));

        Assert.Equal([$"days[0].bands[0].{field}"], ex.Fields);
    }

    [Fact]
    public void NormalizeHours_OverlappingBands_AreRefusedNamingTheLaterOne()
    {
        var input = Hours(Day(DayOfWeek.Monday, Band(14 * 60, 18 * 60), Band(8 * 60, 13 * 60 + 30), Band(13 * 60, 15 * 60)));

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(input));

        Assert.Equal(["days[0].bands[2]", "days[0].bands[0]"], ex.Fields);
    }

    [Fact]
    public void NormalizeHours_ARepeatedWeekdayAMissingWeekdayAndANullDay_AreRefused()
    {
        var input = new SupplierHoursInput(
        [
            Day(DayOfWeek.Monday, Band(480, 600)),
            Day(DayOfWeek.Monday, Band(480, 600)),
            new SupplierHoursDayInput(null, [Band(480, 600)]),
            null,
        ]);

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(input));

        Assert.Equal(["days[1].weekday", "days[2].weekday", "days[3]"], ex.Fields);
    }

    [Fact]
    public void NormalizeHours_ANullBand_IsRefused()
    {
        var input = new SupplierHoursInput([new SupplierHoursDayInput(DayOfWeek.Friday, [null, Band(480, 600)])]);

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(input));

        Assert.Equal(["days[0].bands[0]"], ex.Fields);
    }

    [Fact]
    public void NormalizeHours_NoDaysAtAll_IsRefusedNamingDays()
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(new SupplierHoursInput(null)));

        Assert.Equal(["days"], ex.Fields);
    }

    [Fact]
    public void NormalizeHours_MoreThanSevenDays_IsRefused()
    {
        var days = Enumerable.Range(0, 8).Select(_ => Day(DayOfWeek.Monday)).ToArray();

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(new SupplierHoursInput(days)));

        Assert.Contains("days", ex.Fields);
    }

    [Fact]
    public void NormalizeHours_EveryProblemIsCollected_NotJustTheFirst()
    {
        var input = Hours(
            Day(DayOfWeek.Monday, Band(600, 540)),
            Day(DayOfWeek.Tuesday, Band(-5, 600)),
            Day(DayOfWeek.Wednesday, Band(480, 600), Band(540, 660)));

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeHours(input));

        Assert.Equal(["days[0].bands[0].endMinute", "days[1].bands[0].startMinute", "days[2].bands[1]"], ex.Fields);
        Assert.Equal("days[0].bands[0].endMinute, days[1].bands[0].startMinute, days[2].bands[1]", ex.MessageArgs.Single());
    }

    [Fact]
    public void NormalizeTimeOff_AValidPeriod_IsReturnedWithTheReasonAndATrimmedLabel()
    {
        var content = SupplierAgendaRules.NormalizeTimeOff(
            new SupplierTimeOffInput(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 2), SupplierTimeOffReason.Holiday, "  Ponte di Ognissanti  "),
            Today);

        Assert.Equal(new SupplierTimeOffContent(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 2), SupplierTimeOffReason.Holiday, "Ponte di Ognissanti"), content);
    }

    [Fact]
    public void NormalizeTimeOff_WithoutAReasonOrALabel_IsVacationWithNoLabel()
    {
        var content = SupplierAgendaRules.NormalizeTimeOff(
            new SupplierTimeOffInput(Today, Today, null, "   "), Today);

        Assert.Equal(SupplierTimeOffReason.Vacation, content.Reason);
        Assert.Null(content.Label);
    }

    [Theory]
    [InlineData("2026-10-08", "2026-10-08", true)]
    [InlineData("2026-10-09", "2026-10-08", false)]
    [InlineData("2026-10-07", "2026-10-07", false)]
    [InlineData("2026-10-01", "2026-10-08", true)]
    [InlineData("2026-10-08", "2027-10-08", true)]
    [InlineData("2026-10-08", "2027-10-09", false)]
    public void NormalizeTimeOff_TheDates_AreCheckedAgainstTodayAndTheLength(string from, string to, bool valid)
    {
        var input = new SupplierTimeOffInput(
            DateOnly.Parse(from, CultureInfo.InvariantCulture),
            DateOnly.Parse(to, CultureInfo.InvariantCulture),
            null,
            null);

        if (valid)
        {
            Assert.Equal(DateOnly.Parse(to, CultureInfo.InvariantCulture), SupplierAgendaRules.NormalizeTimeOff(input, Today).ToDate);
        }
        else
        {
            var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeTimeOff(input, Today));
            Assert.Equal(SupplierAgendaErrors.TimeOffInvalid, ex.Code);
            Assert.Equal("SupplierTimeOffInvalid", ex.MessageKey);
            Assert.Equal(["toDate"], ex.Fields);
        }
    }

    [Fact]
    public void NormalizeTimeOff_MissingDates_AreRefusedNamingThem()
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeTimeOff(new SupplierTimeOffInput(null, null, null, null), Today));

        Assert.Equal(["fromDate", "toDate"], ex.Fields);
    }

    [Theory]
    [InlineData(730, true)]
    [InlineData(731, false)]
    public void NormalizeTimeOff_TheFirstDay_CannotBeMoreThanTwoYearsAhead(int daysAhead, bool valid)
    {
        var start = Today.AddDays(daysAhead);
        var input = new SupplierTimeOffInput(start, start, null, null);

        if (valid)
            Assert.Equal(start, SupplierAgendaRules.NormalizeTimeOff(input, Today).FromDate);
        else
            Assert.Equal(["fromDate"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeTimeOff(input, Today)).Fields);
    }

    [Fact]
    public void NormalizeTimeOff_AnUnknownReason_IsRefused()
    {
        var input = new SupplierTimeOffInput(Today, Today, (SupplierTimeOffReason)42, null);

        Assert.Equal(["reason"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeTimeOff(input, Today)).Fields);
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void NormalizeTimeOff_TheLabel_HasAtMostEightyCharacters(int length, bool valid)
    {
        var input = new SupplierTimeOffInput(Today, Today, null, new string('x', length));

        if (valid)
            Assert.Equal(length, SupplierAgendaRules.NormalizeTimeOff(input, Today).Label!.Length);
        else
            Assert.Equal(["label"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeTimeOff(input, Today)).Fields);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u0000b")]
    public void NormalizeTimeOff_ALabelWithControlCharacters_IsRefused(string label)
    {
        var input = new SupplierTimeOffInput(Today, Today, null, label);

        Assert.Equal(["label"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeTimeOff(input, Today)).Fields);
    }

    [Fact]
    public void NormalizeBlock_AValidBlock_IsReturnedInUtcWithATrimmedLabel()
    {
        var content = SupplierAgendaRules.NormalizeBlock(
            new SupplierBlockInput(SupplierBusyWindowKind.Block, Utc("2026-10-09T13:00:00Z"), Utc("2026-10-09T14:30:00Z"), " Dentista "),
            Now);

        Assert.Equal(
            new SupplierBlockContent(SupplierBusyWindowKind.Block, Utc("2026-10-09T13:00:00Z"), Utc("2026-10-09T14:30:00Z"), "Dentista"),
            content);
        Assert.Equal(DateTimeKind.Utc, content.StartUtc.Kind);
    }

    [Fact]
    public void NormalizeBlock_AnUnspecifiedInstant_IsTakenAsUtc_AndALocalOneIsConverted()
    {
        var unspecified = SupplierAgendaRules.NormalizeBlock(
            new SupplierBlockInput(
                SupplierBusyWindowKind.Block,
                new DateTime(2026, 10, 9, 13, 0, 0, DateTimeKind.Unspecified),
                new DateTime(2026, 10, 9, 14, 0, 0, DateTimeKind.Unspecified),
                null),
            Now);

        Assert.Equal(Utc("2026-10-09T13:00:00Z"), unspecified.StartUtc);
        Assert.Equal(DateTimeKind.Utc, unspecified.StartUtc.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SupplierBusyWindowKind.External)]
    [InlineData((SupplierBusyWindowKind)9)]
    public void NormalizeBlock_AKindTheSupplierCannotCreate_IsRefused(SupplierBusyWindowKind? kind)
    {
        var input = new SupplierBlockInput(kind, Utc("2026-10-09T13:00:00Z"), Utc("2026-10-09T14:00:00Z"), null);

        var ex = Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeBlock(input, Now));

        Assert.Equal(SupplierAgendaErrors.BlockInvalid, ex.Code);
        Assert.Equal("SupplierBlockInvalid", ex.MessageKey);
        Assert.Equal(["kind"], ex.Fields);
    }

    [Fact]
    public void NormalizeBlock_MissingInstants_AreRefusedNamingThem()
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeBlock(new SupplierBlockInput(SupplierBusyWindowKind.Block, null, null, null), Now));

        Assert.Equal(["startUtc", "endUtc"], ex.Fields);
    }

    [Theory]
    [InlineData("2026-10-09T13:00:00Z", "2026-10-09T13:00:00Z", false)]
    [InlineData("2026-10-09T13:00:00Z", "2026-10-09T12:00:00Z", false)]
    [InlineData("2026-10-09T13:00:00Z", "2026-10-09T13:14:00Z", false)]
    [InlineData("2026-10-09T13:00:00Z", "2026-10-09T13:15:00Z", true)]
    [InlineData("2026-10-09T13:00:00Z", "2026-11-09T13:00:00Z", true)]
    [InlineData("2026-10-09T13:00:00Z", "2026-11-09T13:01:00Z", false)]
    public void NormalizeBlock_TheLengthOfABlock_IsBetweenFifteenMinutesAndThirtyOneDays(string start, string end, bool valid)
    {
        var input = new SupplierBlockInput(SupplierBusyWindowKind.Block, Utc(start), Utc(end), null);

        if (valid)
            Assert.Equal(Utc(end), SupplierAgendaRules.NormalizeBlock(input, Now).EndUtc);
        else
            Assert.Equal(["endUtc"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeBlock(input, Now)).Fields);
    }

    [Theory]
    [InlineData("2026-10-09T20:00:00Z", "2026-10-09T22:00:00Z", true)]
    [InlineData("2026-10-09T20:00:00Z", "2026-10-09T22:01:00Z", false)]
    [InlineData("2026-10-09T08:00:00Z", "2026-10-09T09:00:00Z", true)]
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z", true)]
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25T23:01:00Z", false)]
    [InlineData("2026-10-09T22:30:00Z", "2026-10-10T21:30:00Z", true)]
    [InlineData("2026-10-09T22:30:00Z", "2026-10-10T22:30:00Z", false)]
    public void NormalizeBlock_AnExtraOpening_StaysInsideOneDayOfRome(string start, string end, bool valid)
    {
        var input = new SupplierBlockInput(SupplierBusyWindowKind.ExtraOpening, Utc(start), Utc(end), null);

        if (valid)
            Assert.Equal(Utc(end), SupplierAgendaRules.NormalizeBlock(input, Now).EndUtc);
        else
            Assert.Equal(["endUtc"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeBlock(input, Now)).Fields);
    }

    [Fact]
    public void NormalizeBlock_ABlockCanCrossMidnight_AnExtraOpeningCannot()
    {
        var start = Utc("2026-10-09T20:00:00Z");
        var end = Utc("2026-10-10T06:00:00Z");

        Assert.Equal(end, SupplierAgendaRules.NormalizeBlock(new SupplierBlockInput(SupplierBusyWindowKind.Block, start, end, null), Now).EndUtc);
        Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeBlock(new SupplierBlockInput(SupplierBusyWindowKind.ExtraOpening, start, end, null), Now));
    }

    [Theory]
    [InlineData("2026-10-08T09:59:00Z", "2026-10-08T10:00:00Z", false)]
    [InlineData("2026-10-08T09:00:00Z", "2026-10-08T10:15:00Z", true)]
    [InlineData("2026-10-01T09:00:00Z", "2026-10-07T10:00:00Z", false)]
    public void NormalizeBlock_ABlockThatIsOver_IsRefused(string start, string end, bool valid)
    {
        var input = new SupplierBlockInput(SupplierBusyWindowKind.Block, Utc(start), Utc(end), null);

        if (valid)
            Assert.Equal(Utc(end), SupplierAgendaRules.NormalizeBlock(input, Now).EndUtc);
        else
            Assert.Contains("endUtc", Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeBlock(input, Now)).Fields);
    }

    [Theory]
    [InlineData(730, true)]
    [InlineData(731, false)]
    public void NormalizeBlock_TheStart_CannotBeMoreThanTwoYearsAhead(int daysAhead, bool valid)
    {
        var start = Now.AddDays(daysAhead);
        var input = new SupplierBlockInput(SupplierBusyWindowKind.Block, start, start.AddHours(1), null);

        if (valid)
            Assert.Equal(start, SupplierAgendaRules.NormalizeBlock(input, Now).StartUtc);
        else
            Assert.Equal(["startUtc"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeBlock(input, Now)).Fields);
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void NormalizeBlock_TheLabel_HasAtMostEightyCharacters(int length, bool valid)
    {
        var input = new SupplierBlockInput(
            SupplierBusyWindowKind.Block, Utc("2026-10-09T13:00:00Z"), Utc("2026-10-09T14:00:00Z"), new string('x', length));

        if (valid)
            Assert.Equal(length, SupplierAgendaRules.NormalizeBlock(input, Now).Label!.Length);
        else
            Assert.Equal(["label"], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeBlock(input, Now)).Fields);
    }

    [Fact]
    public void NormalizeRules_TheDefaults_AreValid()
    {
        var content = SupplierAgendaRules.NormalizeRules(
            new SupplierRulesInput(
                SupplierAgendaDefaults.BufferMinutes,
                SupplierAgendaDefaults.MaxJobsPerDay,
                SupplierAgendaDefaults.MinNoticeHours,
                SupplierAgendaDefaults.HorizonDays,
                SupplierAgendaDefaults.SlotStepMinutes));

        Assert.Equal(new SupplierRulesContent(30, 3, 24, 35, 60), content);
    }

    [Theory]
    [InlineData(0, 1, 0, 1, 15)]
    [InlineData(240, 50, 720, 365, 240)]
    public void NormalizeRules_TheLimits_AreAccepted(int buffer, int maxJobs, int notice, int horizon, int step)
    {
        var content = SupplierAgendaRules.NormalizeRules(new SupplierRulesInput(buffer, maxJobs, notice, horizon, step));

        Assert.Equal(new SupplierRulesContent(buffer, maxJobs, notice, horizon, step), content);
    }

    [Theory]
    [InlineData(-5, "bufferMinutes")]
    [InlineData(245, "bufferMinutes")]
    [InlineData(7, "bufferMinutes")]
    [InlineData(null, "bufferMinutes")]
    public void NormalizeRules_TheBuffer_IsZeroToTwoHundredFortyInFives(int? buffer, string field)
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeRules(new SupplierRulesInput(buffer, 3, 24, 35, 60)));

        Assert.Equal(SupplierAgendaErrors.RulesInvalid, ex.Code);
        Assert.Equal("SupplierRulesInvalid", ex.MessageKey);
        Assert.Equal([field], ex.Fields);
    }

    [Theory]
    [InlineData(0, "maxJobsPerDay")]
    [InlineData(51, "maxJobsPerDay")]
    [InlineData(null, "maxJobsPerDay")]
    public void NormalizeRules_TheDailyMaximum_IsOneToFifty(int? maxJobs, string field)
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeRules(new SupplierRulesInput(30, maxJobs, 24, 35, 60)));

        Assert.Equal([field], ex.Fields);
    }

    [Theory]
    [InlineData(-1, "minNoticeHours")]
    [InlineData(721, "minNoticeHours")]
    [InlineData(null, "minNoticeHours")]
    public void NormalizeRules_TheNotice_IsZeroToThirtyDays(int? notice, string field)
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeRules(new SupplierRulesInput(30, 3, notice, 35, 60)));

        Assert.Equal([field], ex.Fields);
    }

    [Theory]
    [InlineData(0, "horizonDays")]
    [InlineData(366, "horizonDays")]
    [InlineData(null, "horizonDays")]
    public void NormalizeRules_TheHorizon_IsOneDayToOneYear(int? horizon, string field)
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeRules(new SupplierRulesInput(30, 3, 24, horizon, 60)));

        Assert.Equal([field], ex.Fields);
    }

    [Theory]
    [InlineData(10, "slotStepMinutes")]
    [InlineData(245, "slotStepMinutes")]
    [InlineData(20, null)]
    [InlineData(25, null)]
    [InlineData(22, "slotStepMinutes")]
    [InlineData(null, "slotStepMinutes")]
    public void NormalizeRules_TheSlotStep_IsFifteenToTwoHundredFortyInFives(int? step, string? field)
    {
        var input = new SupplierRulesInput(30, 3, 24, 35, step);

        if (field is null)
            Assert.Equal(step, SupplierAgendaRules.NormalizeRules(input).SlotStepMinutes);
        else
            Assert.Equal([field], Assert.Throws<SupplierAgendaRuleException>(() => SupplierAgendaRules.NormalizeRules(input)).Fields);
    }

    [Fact]
    public void NormalizeRules_EveryRuleMissing_IsRefusedNamingAllFive()
    {
        var ex = Assert.Throws<SupplierAgendaRuleException>(
            () => SupplierAgendaRules.NormalizeRules(new SupplierRulesInput(null, null, null, null, null)));

        Assert.Equal(
            ["bufferMinutes", "maxJobsPerDay", "minNoticeHours", "horizonDays", "slotStepMinutes"],
            ex.Fields);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 0)]
    [InlineData(DayOfWeek.Wednesday, 2)]
    [InlineData(DayOfWeek.Saturday, 5)]
    [InlineData(DayOfWeek.Sunday, 6)]
    public void MondayFirst_IsThePositionInAnItalianWeek(DayOfWeek day, int expected)
    {
        Assert.Equal(expected, SupplierAgendaRules.MondayFirst(day));
    }

    [Fact]
    public void TheDefaults_AreTheOnesOfTheSpec()
    {
        Assert.Equal(new SupplierPlanningRules(30, 3, 24, 35, 60, 1), SupplierPlanningRules.Default);
        Assert.Equal(180, SupplierAgendaDefaults.RespondWithinMinutes);
    }

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static SupplierHoursBandInput Band(int start, int end) => new(start, end);

    private static SupplierHoursDayInput Day(DayOfWeek weekday, params SupplierHoursBandInput[] bands) => new(weekday, bands);

    private static SupplierHoursInput Hours(params SupplierHoursDayInput[] days) => new(days);
}
