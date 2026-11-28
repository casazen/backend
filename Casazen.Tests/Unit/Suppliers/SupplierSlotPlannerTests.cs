using System.Globalization;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class SupplierSlotPlannerTests
{
    private static readonly DateTime Now = Utc("2026-10-05T07:00:00Z");

    private static readonly DateOnly Mon = new(2026, 10, 5);
    private static readonly DateOnly Tue = new(2026, 10, 6);
    private static readonly DateOnly Wed = new(2026, 10, 7);
    private static readonly DateOnly Sat = new(2026, 10, 10);
    private static readonly DateOnly Sun = new(2026, 10, 11);

    private static readonly SupplierWeeklyBand[] DemoWeek =
    [
        .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .SelectMany(day => new[]
            {
                new SupplierWeeklyBand(day, 8 * 60, 13 * 60),
                new SupplierWeeklyBand(day, 14 * 60, 18 * 60),
            }),
        new SupplierWeeklyBand(DayOfWeek.Saturday, 8 * 60, 14 * 60),
    ];

    [Fact]
    public void PlanDay_ADayBeforeToday_IsPast()
    {
        var plan = Plan(Mon.AddDays(-1));

        Assert.Equal(SupplierDayClosure.Past, plan.Closure);
        Assert.True(plan.IsClosed);
        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void PlanDay_TodayIsDecidedOnTheRomeCalendarNotTheUtcOne()
    {
        var late = Input(now: Utc("2026-10-05T22:30:00Z"), rules: Rules(noticeHours: 0));

        Assert.Equal(SupplierDayClosure.Past, SupplierSlotPlanner.PlanDay(Mon, late, Query()).Closure);
        Assert.Null(SupplierSlotPlanner.PlanDay(Tue, late, Query()).Closure);
    }

    [Fact]
    public void PlanDay_TheWholeDayInsideTheNotice_IsWithinNotice_AndTheNextDayOpensAtNowPlusNotice()
    {
        var monday = Plan(Mon);
        var tuesday = Plan(Tue);

        Assert.Equal(SupplierDayClosure.WithinNotice, monday.Closure);
        Assert.Null(tuesday.Closure);
        Assert.Equal(
            ["09:00", "10:00", "11:00", "12:00", "14:00", "15:00", "16:00", "17:00"],
            Starts(tuesday));
    }

    [Theory]
    [InlineData("2026-10-05T07:00:00Z", "09:00")]
    [InlineData("2026-10-05T07:01:00Z", "10:00")]
    public void PlanDay_ASlotStartingExactlyAtNowPlusNotice_IsOffered_OneMinuteSooner_IsNot(string now, string firstSlot)
    {
        var plan = SupplierSlotPlanner.PlanDay(Tue, Input(now: Utc(now)), Query(duration: 60));

        Assert.Equal(firstSlot, Starts(plan).First());
    }

    [Fact]
    public void PlanDay_TheNoticeOfTheService_ReplacesTheSuppliersOwn()
    {
        var none = SupplierSlotPlanner.PlanDay(Mon, Input(), Query(noticeHours: 0));
        var week = SupplierSlotPlanner.PlanDay(Wed, Input(), Query(noticeHours: 72));
        var longer = SupplierSlotPlanner.PlanDay(Sat, Input(), Query(noticeHours: 72));

        Assert.Null(none.Closure);
        Assert.Equal("09:00", Starts(none).First());
        Assert.Equal(SupplierDayClosure.WithinNotice, week.Closure);
        Assert.Null(longer.Closure);
    }

    [Theory]
    [InlineData("2026-10-06", false)]
    [InlineData("2026-10-07", false)]
    [InlineData("2026-10-08", false)]
    [InlineData("2026-10-09", true)]
    public void PlanDay_ADayInATimeOff_IsClosed_BothEndsIncluded(string day, bool open)
    {
        var input = Input(timeOff: [new SupplierDateRange(Tue, new DateOnly(2026, 10, 8))]);

        var plan = SupplierSlotPlanner.PlanDay(DateOnly.Parse(day, CultureInfo.InvariantCulture), input, Query());

        Assert.Equal(open ? (SupplierDayClosure?)null : SupplierDayClosure.TimeOff, plan.Closure);
        Assert.Equal(open, plan.Slots.Count > 0);
    }

    [Fact]
    public void PlanDay_ADayClosedByHandOrByTheCalendarFeed_IsDayClosed()
    {
        var input = Input(closedDays: new HashSet<DateOnly> { Wed });

        Assert.Equal(SupplierDayClosure.DayClosed, SupplierSlotPlanner.PlanDay(Wed, input, Query()).Closure);
        Assert.Null(SupplierSlotPlanner.PlanDay(Tue, input, Query()).Closure);
    }

    [Fact]
    public void PlanDay_AWeekdayTheServiceIsNotOfferedOn_IsServiceNotOffered_EvenWithExtraOpenings()
    {
        var mask = SupplierServiceWeekdays.ToMask([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Thursday]);
        var input = Input(extra: [new SupplierInterval(Utc("2026-10-07T15:00:00Z"), Utc("2026-10-07T17:00:00Z"))]);

        var plan = SupplierSlotPlanner.PlanDay(Wed, input, Query(weekdaysMask: mask));

        Assert.Equal(SupplierDayClosure.ServiceNotOffered, plan.Closure);
        Assert.Null(SupplierSlotPlanner.PlanDay(Tue, input, Query(weekdaysMask: mask)).Closure);
    }

    [Fact]
    public void PlanDay_ADayWithoutBands_IsNoHours_AndAnExtraOpeningOpensIt()
    {
        var restDay = Plan(Sun);

        var opened = SupplierSlotPlanner.PlanDay(
            Sun,
            Input(extra: [new SupplierInterval(Utc("2026-10-11T08:00:00Z"), Utc("2026-10-11T10:00:00Z"))]),
            Query());

        Assert.Equal(SupplierDayClosure.NoHours, restDay.Closure);
        Assert.Null(opened.Closure);
        Assert.Equal(["10:00", "11:00"], Starts(opened));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void PlanDay_TheDailyMaximumReached_ClosesTheDay(int requestsThatDay, bool closed)
    {
        var requests = Enumerable.Repeat(SupplierOccupancy.DatedRequest(Wed), requestsThatDay).ToList();

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(occupancies: requests), Query());

        Assert.Equal(closed ? SupplierDayClosure.MaxJobsReached : (SupplierDayClosure?)null, plan.Closure);
        Assert.Equal(!closed, plan.Slots.Count > 0);
    }

    [Fact]
    public void PlanDay_RequestsWithoutHours_CountForTheMaximumButTakeNoHour()
    {
        var two = Input(occupancies: [SupplierOccupancy.DatedRequest(Wed), SupplierOccupancy.DatedRequest(Wed)]);

        var plan = SupplierSlotPlanner.PlanDay(Wed, two, Query());

        Assert.Null(plan.Closure);
        Assert.Equal(["08:00", "09:00", "10:00", "11:00", "12:00", "14:00", "15:00", "16:00", "17:00"], Starts(plan));
    }

    [Fact]
    public void PlanDay_TimedRequestsAndHoldsCountForTheMaximum_BlocksAndEngagementsDoNot()
    {
        var jobs = Input(occupancies:
        [
            SupplierOccupancy.TimedRequest(Utc("2026-10-07T06:00:00Z"), Utc("2026-10-07T07:00:00Z")),
            SupplierOccupancy.Hold(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"), Utc("2026-10-05T08:00:00Z")),
            SupplierOccupancy.DatedRequest(Wed),
        ]);
        var notJobs = Input(occupancies:
        [
            SupplierOccupancy.Block(Utc("2026-10-07T06:00:00Z"), Utc("2026-10-07T07:00:00Z")),
            SupplierOccupancy.External(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z")),
            SupplierOccupancy.Block(Utc("2026-10-07T12:00:00Z"), Utc("2026-10-07T13:00:00Z")),
            SupplierOccupancy.External(Utc("2026-10-07T13:30:00Z"), Utc("2026-10-07T14:00:00Z")),
        ]);

        Assert.Equal(SupplierDayClosure.MaxJobsReached, SupplierSlotPlanner.PlanDay(Wed, jobs, Query()).Closure);
        Assert.Null(SupplierSlotPlanner.PlanDay(Wed, notJobs, Query()).Closure);
    }

    [Fact]
    public void PlanDay_TheJobsOfADayAreThoseOfItsRomeDay()
    {
        var lateOnTuesdayUtc = SupplierOccupancy.TimedRequest(Utc("2026-10-06T22:30:00Z"), Utc("2026-10-06T23:30:00Z"));
        var input = Input(occupancies: [lateOnTuesdayUtc, lateOnTuesdayUtc, lateOnTuesdayUtc]);

        Assert.Null(SupplierSlotPlanner.PlanDay(Tue, input, Query()).Closure);
        Assert.Equal(SupplierDayClosure.MaxJobsReached, SupplierSlotPlanner.PlanDay(Wed, input, Query()).Closure);
    }

    [Theory]
    [InlineData("2026-11-09", null)]
    [InlineData("2026-11-10", SupplierDayClosure.BeyondHorizon)]
    public void PlanDay_TheHorizon_IncludesTodayPlusHorizonDays(string day, SupplierDayClosure? closure)
    {
        var plan = SupplierSlotPlanner.PlanDay(DateOnly.Parse(day, CultureInfo.InvariantCulture), Input(), Query());

        Assert.Equal(closure, plan.Closure);
    }

    [Fact]
    public void PlanDay_ADayClosedForSeveralReasons_ShowsTheFirstOne()
    {
        var wed = Wed;
        var input = Input(
            timeOff: [new SupplierDateRange(wed, wed)],
            closedDays: new HashSet<DateOnly> { wed },
            occupancies: [SupplierOccupancy.DatedRequest(wed), SupplierOccupancy.DatedRequest(wed), SupplierOccupancy.DatedRequest(wed)]);

        Assert.Equal(SupplierDayClosure.TimeOff, SupplierSlotPlanner.PlanDay(wed, input, Query()).Closure);
        Assert.Equal(
            SupplierDayClosure.Past,
            SupplierSlotPlanner.PlanDay(Mon.AddDays(-2), Input(timeOff: [new SupplierDateRange(Mon.AddDays(-2), Mon)]), Query()).Closure);
        Assert.Equal(
            SupplierDayClosure.DayClosed,
            SupplierSlotPlanner.PlanDay(wed, input with { TimeOff = [] }, Query()).Closure);
        Assert.Equal(
            SupplierDayClosure.MaxJobsReached,
            SupplierSlotPlanner.PlanDay(wed, input with { TimeOff = [], ClosedDays = new HashSet<DateOnly>() }, Query()).Closure);
    }

    [Fact]
    public void PlanDay_AnOpenDay_HasASlotAtTheStartOfEachBandThenEveryStep_AsInstantsOfRome()
    {
        var plan = Plan(Wed);

        Assert.Null(plan.Closure);
        Assert.Equal(Utc("2026-10-07T06:00:00Z"), plan.Slots[0].StartUtc);
        Assert.Equal(Utc("2026-10-07T07:00:00Z"), plan.Slots[0].EndUtc);
        Assert.Equal(["08:00", "09:00", "10:00", "11:00", "12:00", "14:00", "15:00", "16:00", "17:00"], Starts(plan));
        Assert.All(plan.Slots, slot => Assert.Equal(TimeSpan.FromMinutes(60), slot.EndUtc - slot.StartUtc));
    }

    [Theory]
    [InlineData(30, 30, 10, 8)]
    [InlineData(60, 60, 5, 4)]
    [InlineData(90, 60, 4, 3)]
    [InlineData(300, 60, 1, 0)]
    [InlineData(301, 60, 0, 0)]
    [InlineData(60, 90, 3, 3)]
    public void PlanDay_SlotsFitInsideTheBand_AtTheStepOfTheSupplier(int duration, int step, int morning, int afternoon)
    {
        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(stepMinutes: step, bufferMinutes: 0)), Query(duration: duration));

        var local = Starts(plan);
        Assert.Equal(morning, local.Count(time => string.CompareOrdinal(time, "13:00") < 0));
        Assert.Equal(afternoon, local.Count(time => string.CompareOrdinal(time, "13:00") >= 0));
    }

    [Fact]
    public void PlanDay_AStepThatDoesNotDivideTheBand_AnchorsTheGridAtTheStartOfTheBand()
    {
        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(stepMinutes: 45, bufferMinutes: 0)), Query(duration: 45));

        Assert.Equal(["08:00", "08:45", "09:30", "10:15", "11:00", "11:45"], Starts(plan).Where(t => string.CompareOrdinal(t, "13:00") < 0));
    }

    [Fact]
    public void PlanRange_ReturnsOnePlanPerDay_InDateOrder()
    {
        var plans = SupplierSlotPlanner.PlanRange(Tue, Sun, Input(), Query());

        Assert.Equal(6, plans.Count);
        Assert.Equal(new[] { Tue, Wed, Tue.AddDays(2), Tue.AddDays(3), Sat, Sun }, plans.Select(plan => plan.Day));
        Assert.Equal(SupplierDayClosure.NoHours, plans[^1].Closure);
        Assert.Equal(["08:00", "09:00", "10:00", "11:00", "12:00", "13:00"], Starts(plans[^2]));
    }

    [Fact]
    public void PlanDay_ABlock_TakesItsHoursAndTheBufferOnBothSides()
    {
        var block = SupplierOccupancy.Block(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(occupancies: [block]), Query());

        Assert.Equal(["08:00", "12:00", "14:00", "15:00", "16:00", "17:00"], Starts(plan));
    }

    [Fact]
    public void PlanDay_WithoutBuffer_OnlyTheOverlappingSlotIsTaken()
    {
        var block = SupplierOccupancy.Block(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [block]), Query());

        Assert.Equal(["08:00", "09:00", "11:00", "12:00", "14:00", "15:00", "16:00", "17:00"], Starts(plan));
    }

    [Theory]
    [InlineData(0, "12:00")]
    [InlineData(15, "12:30")]
    public void PlanDay_TheBufferWidensTheJob_AndDoesNotPushTheFirstSlotOfTheBand(int buffer, string firstAfter)
    {
        var job = SupplierOccupancy.TimedRequest(Utc("2026-10-07T09:00:00Z"), Utc("2026-10-07T10:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(
            Wed,
            Input(rules: Rules(bufferMinutes: buffer, stepMinutes: 30), occupancies: [job]),
            Query(duration: 30));

        var morning = Starts(plan).Where(t => string.CompareOrdinal(t, "13:00") < 0).ToList();
        Assert.Equal(firstAfter, morning.First(t => string.CompareOrdinal(t, "11:00") > 0));
        Assert.Equal("08:00", morning.First());
    }

    [Theory]
    [InlineData(SupplierOccupancyKind.Request)]
    [InlineData(SupplierOccupancyKind.Hold)]
    [InlineData(SupplierOccupancyKind.Block)]
    [InlineData(SupplierOccupancyKind.External)]
    public void PlanDay_RequestsHoldsBlocksAndEngagementsWithHours_AllTakeTheirSlot(SupplierOccupancyKind kind)
    {
        var start = Utc("2026-10-07T08:00:00Z");
        var end = Utc("2026-10-07T09:00:00Z");
        var occupancy = kind switch
        {
            SupplierOccupancyKind.Request => SupplierOccupancy.TimedRequest(start, end),
            SupplierOccupancyKind.Hold => SupplierOccupancy.Hold(start, end, Now.AddMinutes(30)),
            SupplierOccupancyKind.Block => SupplierOccupancy.Block(start, end),
            _ => SupplierOccupancy.External(start, end),
        };

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [occupancy]), Query());

        Assert.DoesNotContain("10:00", Starts(plan));
        Assert.Contains("09:00", Starts(plan));
        Assert.Contains("11:00", Starts(plan));
    }

    [Fact]
    public void PlanDay_AnEngagementOfTheCalendarFeedFrom10To11_TakesThatHourOnly_NotTheDay()
    {
        // SP-05: the iCal sync turns a 10:00-11:00 event into an External stretch (08:00-09:00 UTC) instead of closing the
        // day, so the day is open and every slot that clears the event (and the buffer around it) is still offered.
        var engagement = SupplierOccupancy.External(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"));

        var noBuffer = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [engagement]), Query());
        var withBuffer = SupplierSlotPlanner.PlanDay(Wed, Input(occupancies: [engagement]), Query());

        Assert.Null(noBuffer.Closure);
        Assert.Equal(["08:00", "09:00", "11:00", "12:00", "14:00", "15:00", "16:00", "17:00"], Starts(noBuffer));
        Assert.Null(withBuffer.Closure);
        Assert.Equal(["08:00", "12:00", "14:00", "15:00", "16:00", "17:00"], Starts(withBuffer));
        Assert.Equal(
            SupplierDayClosure.DayClosed,
            SupplierSlotPlanner.PlanDay(Wed, Input(closedDays: new HashSet<DateOnly> { Wed }, occupancies: [engagement]), Query()).Closure);
    }

    [Fact]
    public void PlanDay_ADayWhoseEveryHourIsTaken_IsOpenWithNoSlot_NotClosed()
    {
        var allDay = SupplierOccupancy.Block(Utc("2026-10-07T06:00:00Z"), Utc("2026-10-07T16:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [allDay]), Query());

        Assert.Null(plan.Closure);
        Assert.False(plan.IsClosed);
        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void PlanDay_AHoldThatExpired_IsIgnored_AndNoLongerCountsForTheMaximum()
    {
        var expired = SupplierOccupancy.Hold(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"), Now);
        var alive = SupplierOccupancy.Hold(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"), Now.AddMinutes(1));

        Assert.Contains("10:00", Starts(SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [expired]), Query())));
        Assert.DoesNotContain("10:00", Starts(SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [alive]), Query())));
        Assert.Null(SupplierSlotPlanner.PlanDay(Wed, Input(occupancies: [expired, expired, expired]), Query()).Closure);
        Assert.Equal(
            SupplierDayClosure.MaxJobsReached,
            SupplierSlotPlanner.PlanDay(Wed, Input(occupancies: [alive, alive, alive]), Query()).Closure);
    }

    [Fact]
    public void PlanDay_ABlockThatEndsBeforeTheBandStarts_StillTakesTheFirstSlotThroughItsBuffer()
    {
        var block = SupplierOccupancy.Block(Utc("2026-10-07T04:00:00Z"), Utc("2026-10-07T05:45:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(occupancies: [block]), Query());

        Assert.Equal("09:00", Starts(plan).First());
    }

    [Fact]
    public void PlanDay_WithTwoJobsAtOnce_OneJobLeavesRoom_TwoOverlappingOnesDoNot()
    {
        var one = SupplierOccupancy.TimedRequest(Utc("2026-10-07T08:00:00Z"), Utc("2026-10-07T09:00:00Z"));
        var other = SupplierOccupancy.Block(Utc("2026-10-07T08:30:00Z"), Utc("2026-10-07T09:30:00Z"));
        var rules = Rules(bufferMinutes: 0, parallelJobs: 2);

        var withOne = SupplierSlotPlanner.PlanDay(Wed, Input(rules: rules, occupancies: [one]), Query());
        var withTwo = SupplierSlotPlanner.PlanDay(Wed, Input(rules: rules, occupancies: [one, other]), Query());
        var single = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(bufferMinutes: 0), occupancies: [one]), Query());

        Assert.Contains("10:00", Starts(withOne));
        Assert.DoesNotContain("10:00", Starts(withTwo));
        Assert.DoesNotContain("10:00", Starts(single));
    }

    [Fact]
    public void PlanDay_WithTwoJobsAtOnce_JobsThatFollowEachOtherNeverFillTheCapacity()
    {
        // 09:00-10:00 and 11:00-12:00 with a 30-minute buffer: each is taken from 30 minutes before to 30 after, so they touch
        // at 10:30 but never overlap. The slot 10:00-11:00 meets one and then the other, never both: with two jobs at once it
        // is free; with one job at a time it is not.
        var first = SupplierOccupancy.TimedRequest(Utc("2026-10-07T07:00:00Z"), Utc("2026-10-07T08:00:00Z"));
        var second = SupplierOccupancy.TimedRequest(Utc("2026-10-07T09:00:00Z"), Utc("2026-10-07T10:00:00Z"));

        var capacityTwo = SupplierSlotPlanner.PlanDay(
            Wed, Input(rules: Rules(parallelJobs: 2), occupancies: [first, second]), Query());
        var capacityOne = SupplierSlotPlanner.PlanDay(
            Wed, Input(rules: Rules(parallelJobs: 1), occupancies: [first, second]), Query());

        Assert.Contains("10:00", Starts(capacityTwo));
        Assert.DoesNotContain("10:00", Starts(capacityOne));
    }

    [Fact]
    public void PlanDay_TheDailyMaximumIsNotRaisedByTheCapacity()
    {
        var three = Enumerable.Repeat(SupplierOccupancy.DatedRequest(Wed), 3).ToList();

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(parallelJobs: 3), occupancies: three), Query());

        Assert.Equal(SupplierDayClosure.MaxJobsReached, plan.Closure);
    }

    [Fact]
    public void PlanDay_AnExtraOpeningThatTouchesTwoBands_JoinsThemIntoOne_SoALongServiceCanCrossTheSeam()
    {
        var lunch = new SupplierInterval(Utc("2026-10-07T11:00:00Z"), Utc("2026-10-07T12:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(extra: [lunch]), Query(duration: 90));

        Assert.Equal(["08:00", "09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00"], Starts(plan));
    }

    [Fact]
    public void PlanDay_AnExtraOpeningInsideARegularBand_AddsNothing()
    {
        var inside = new SupplierInterval(Utc("2026-10-07T07:00:00Z"), Utc("2026-10-07T09:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(extra: [inside]), Query());

        Assert.Equal(Starts(Plan(Wed)), Starts(plan));
    }

    [Fact]
    public void PlanDay_AnExtraOpeningThatStartsBeforeABand_MovesTheGridToItsStart()
    {
        var early = new SupplierInterval(Utc("2026-10-07T05:30:00Z"), Utc("2026-10-07T06:00:00Z"));

        var plan = SupplierSlotPlanner.PlanDay(Wed, Input(extra: [early]), Query());

        Assert.Equal(["07:30", "08:30", "09:30", "10:30", "11:30"], Starts(plan).Where(t => string.CompareOrdinal(t, "13:00") < 0));
    }

    [Fact]
    public void PlanDay_AnExtraOpening_NeverOpensATimeOffOrAClosedDay()
    {
        var extra = new SupplierInterval(Utc("2026-10-07T15:00:00Z"), Utc("2026-10-07T17:00:00Z"));

        var inTimeOff = SupplierSlotPlanner.PlanDay(Wed, Input(extra: [extra], timeOff: [new SupplierDateRange(Wed, Wed)]), Query());
        var closed = SupplierSlotPlanner.PlanDay(Wed, Input(extra: [extra], closedDays: new HashSet<DateOnly> { Wed }), Query());

        Assert.Equal(SupplierDayClosure.TimeOff, inTimeOff.Closure);
        Assert.Equal(SupplierDayClosure.DayClosed, closed.Closure);
    }

    [Fact]
    public void PlanDay_AnExtraOpeningIsTheDayOfItsStartInRome()
    {
        var night = new SupplierInterval(Utc("2026-10-10T22:30:00Z"), Utc("2026-10-10T23:30:00Z"));
        var input = Input(extra: [night], rules: Rules(stepMinutes: 30));

        Assert.Equal(["00:30", "01:00"], Starts(SupplierSlotPlanner.PlanDay(Sun, input, Query(duration: 30))));
        Assert.DoesNotContain("00:30", Starts(SupplierSlotPlanner.PlanDay(Sat, input, Query(duration: 30))));
    }

    [Fact]
    public void PlanDay_TheDayTheClockGoesForward_OffersNothingInTheSkippedHour()
    {
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 0, 6 * 60) };
        var input = Input(now: Utc("2026-03-25T07:00:00Z"), rules: Rules(noticeHours: 0), hours: week);

        var plan = SupplierSlotPlanner.PlanDay(new DateOnly(2026, 3, 29), input, Query());

        Assert.Equal(
            [
                Utc("2026-03-28T23:00:00Z"),
                Utc("2026-03-29T00:00:00Z"),
                Utc("2026-03-29T01:00:00Z"),
                Utc("2026-03-29T02:00:00Z"),
                Utc("2026-03-29T03:00:00Z"),
            ],
            plan.Slots.Select(slot => slot.StartUtc));
        Assert.Equal(["00:00", "01:00", "03:00", "04:00", "05:00"], LocalTimes(plan));
    }

    [Fact]
    public void PlanDay_TheDayTheClockGoesBack_OffersTheRepeatedHourTwice()
    {
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 0, 6 * 60) };
        var input = Input(now: Utc("2026-10-20T07:00:00Z"), rules: Rules(noticeHours: 0), hours: week);

        var plan = SupplierSlotPlanner.PlanDay(new DateOnly(2026, 10, 25), input, Query());

        Assert.Equal(
            [
                Utc("2026-10-24T22:00:00Z"),
                Utc("2026-10-24T23:00:00Z"),
                Utc("2026-10-25T00:00:00Z"),
                Utc("2026-10-25T01:00:00Z"),
                Utc("2026-10-25T02:00:00Z"),
                Utc("2026-10-25T03:00:00Z"),
                Utc("2026-10-25T04:00:00Z"),
            ],
            plan.Slots.Select(slot => slot.StartUtc));
        Assert.Equal(["00:00", "01:00", "02:00", "02:00", "03:00", "04:00", "05:00"], LocalTimes(plan));
    }

    [Theory]
    [InlineData("2026-03-28", "2026-03-28T08:00:00Z")]
    [InlineData("2026-03-29", "2026-03-29T07:00:00Z")]
    [InlineData("2026-10-24", "2026-10-24T07:00:00Z")]
    [InlineData("2026-10-25", "2026-10-25T08:00:00Z")]
    public void PlanDay_WorkingHoursAreWallClockTimes_SoTheSameHourIsADifferentInstantAcrossTheChange(string date, string firstSlotUtc)
    {
        var week = Enum.GetValues<DayOfWeek>().Select(day => new SupplierWeeklyBand(day, 9 * 60, 12 * 60)).ToArray();
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        var input = Input(now: day.AddDays(-4).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), rules: Rules(noticeHours: 0), hours: week);

        var plan = SupplierSlotPlanner.PlanDay(day, input, Query());

        Assert.Equal(Utc(firstSlotUtc), plan.Slots[0].StartUtc);
        Assert.Equal(3, plan.Slots.Count);
    }

    [Fact]
    public void PlanDay_ABandThatStartsInTheSkippedHourAndEndsRightAfterIt_HasNoLength_AndIsDropped()
    {
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 2 * 60 + 30, 3 * 60 + 10) };
        var input = Input(now: Utc("2026-03-25T07:00:00Z"), rules: Rules(noticeHours: 0), hours: week);

        var plan = SupplierSlotPlanner.PlanDay(new DateOnly(2026, 3, 29), input, Query(duration: 5));

        Assert.Null(plan.Closure);
        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void PlanDay_ABandAcrossTheSkippedHour_IsOnlyAsLongAsTheTimeThatReallyPasses()
    {
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 2 * 60, 3 * 60 + 30) };
        var input = Input(now: Utc("2026-03-25T07:00:00Z"), rules: Rules(noticeHours: 0, stepMinutes: 30, bufferMinutes: 0), hours: week);

        var thirty = SupplierSlotPlanner.PlanDay(new DateOnly(2026, 3, 29), input, Query(duration: 30));
        var sixty = SupplierSlotPlanner.PlanDay(new DateOnly(2026, 3, 29), input, Query(duration: 60));

        Assert.Equal(new SupplierSlot(Utc("2026-03-29T01:00:00Z"), Utc("2026-03-29T01:30:00Z")), Assert.Single(thirty.Slots));
        Assert.Empty(sixty.Slots);
    }

    [Theory]
    [InlineData(17, SupplierDayClosure.WithinNotice)]
    [InlineData(16, null)]
    public void PlanDay_TheDayTheClockGoesForward_IsInsideTheNoticeUntilItsRealEnd(int noticeHours, SupplierDayClosure? closure)
    {
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 9 * 60, 12 * 60) };
        var input = Input(now: Utc("2026-03-29T05:00:00Z"), rules: Rules(noticeHours: noticeHours), hours: week);

        var plan = SupplierSlotPlanner.PlanDay(new DateOnly(2026, 3, 29), input, Query());

        Assert.Equal(closure, plan.Closure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void PlanRange_ADurationThatIsNotPositive_Throws(int duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierSlotPlanner.PlanDay(Wed, Input(), Query(duration: duration)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PlanRange_AStepThatIsNotPositive_ThrowsInsteadOfLooping(int step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierSlotPlanner.PlanDay(Wed, Input(rules: Rules(stepMinutes: step)), Query()));
    }

    [Fact]
    public void PlanRange_ReversedOrTooLong_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierSlotPlanner.PlanRange(Wed, Tue, Input(), Query()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SupplierSlotPlanner.PlanRange(Tue, Tue.AddDays(SupplierSlotPlanner.MaxRangeDays), Input(), Query()));
        Assert.Equal(SupplierSlotPlanner.MaxRangeDays, SupplierSlotPlanner.PlanRange(Tue, Tue.AddDays(SupplierSlotPlanner.MaxRangeDays - 1), Input(), Query()).Count);
    }

    [Fact]
    public void Occupancy_AnIntervalWhoseEndIsNotAfterItsStart_IsRefused()
    {
        var at = Utc("2026-10-07T08:00:00Z");

        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierOccupancy.Block(at, at));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierOccupancy.TimedRequest(at, at.AddMinutes(-1)));
    }

    [Fact]
    public void Occupancy_WhatCountsForTheDailyMaximum_IsRequestsAndHoldsOnly()
    {
        var start = Utc("2026-10-07T08:00:00Z");
        var end = start.AddHours(1);

        Assert.True(SupplierOccupancy.TimedRequest(start, end).CountsTowardsDailyMax);
        Assert.True(SupplierOccupancy.DatedRequest(Wed).CountsTowardsDailyMax);
        Assert.True(SupplierOccupancy.Hold(start, end, end).CountsTowardsDailyMax);
        Assert.False(SupplierOccupancy.Block(start, end).CountsTowardsDailyMax);
        Assert.False(SupplierOccupancy.External(start, end).CountsTowardsDailyMax);
        Assert.False(SupplierOccupancy.DatedRequest(Wed).HasInterval);
        Assert.True(SupplierOccupancy.Block(start, end).HasInterval);
    }

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static SupplierPlanningRules Rules(
        int bufferMinutes = 30,
        int maxJobsPerDay = 3,
        int noticeHours = 24,
        int horizonDays = 35,
        int stepMinutes = 60,
        int parallelJobs = 1) =>
        new(bufferMinutes, maxJobsPerDay, noticeHours, horizonDays, stepMinutes, parallelJobs);

    private static SupplierSlotQuery Query(int duration = 60, int? noticeHours = null, int? weekdaysMask = null) =>
        new(duration, noticeHours, weekdaysMask);

    private static SupplierPlanningInput Input(
        DateTime? now = null,
        SupplierPlanningRules? rules = null,
        IReadOnlyList<SupplierWeeklyBand>? hours = null,
        IReadOnlyList<SupplierDateRange>? timeOff = null,
        IReadOnlySet<DateOnly>? closedDays = null,
        IReadOnlyList<SupplierInterval>? extra = null,
        IReadOnlyList<SupplierOccupancy>? occupancies = null) =>
        new(
            now ?? Now,
            rules ?? Rules(),
            hours ?? DemoWeek,
            timeOff ?? [],
            closedDays ?? new HashSet<DateOnly>(),
            extra ?? [],
            occupancies ?? []);

    private static SupplierDayPlan Plan(DateOnly day, SupplierSlotQuery? query = null) =>
        SupplierSlotPlanner.PlanDay(day, Input(), query ?? Query());

    private static List<string> Starts(SupplierDayPlan plan) => LocalTimes(plan);

    private static List<string> LocalTimes(SupplierDayPlan plan) =>
        plan.Slots
            .Select(slot => TimeZoneInfo.ConvertTimeFromUtc(slot.StartUtc, RomeCalendar.TimeZone).ToString("HH:mm", CultureInfo.InvariantCulture))
            .ToList();
}
