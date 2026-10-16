using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-03b: the conflict detector, a pure function like the planner it asks: the eight codes, what counts against a job, what is
/// not a conflict, the severity and the order, the suggested time, the two days a year the clock changes (29 March and 25 October
/// 2026), and the property that matters most: <b>for a request still waiting for an answer, the detector reports a conflict exactly
/// when the planner would not offer its time</b> (every half hour of a week, many scenarios). No database, no clock.
/// </summary>
/// <remarks>
/// The week of the demo: Monday to Friday 08:00-13:00 and 14:00-18:00, Saturday 08:00-14:00, Sunday a rest day. "Now" is Monday
/// 5 October 2026 at 09:00 in Rome (07:00 UTC, summer time). Times in the cases are the wall clock of Rome.
/// </remarks>
public class SupplierConflictDetectorTests
{
    private static readonly DateTime Now = Utc("2026-10-05T07:00:00Z");

    private const string Tue = "2026-10-06";
    private const string Wed = "2026-10-07";
    private const string Thu = "2026-10-08";
    private const string Fri = "2026-10-09";
    private const string Sun = "2026-10-11";

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

    // ─── The eight codes ─────────────────────────────────────────────────────────

    [Fact]
    public void TimeOff_TheSupplierIsOnLeaveThatDay_IsDanger_AndTheRestOfTheDayIsNotSaidAgain()
    {
        var onSunday = Job(Request(Sun, "10:00", 60)); // a rest day too
        var onWednesday = Job(Request(Wed, "10:00", 60));
        var input = Input([onSunday, onWednesday], timeOff: [new SupplierDateRange(D(Wed), D(Sun))]);

        Assert.Equal([SupplierConflictCodes.TimeOff], Codes(input, onWednesday));
        Assert.Equal([SupplierConflictCodes.TimeOff], Codes(input, onSunday)); // not also day_off
        Assert.Equal(SupplierConflictCodes.Danger, Assert.Single(SupplierConflictDetector.ConflictsOf(input, onWednesday.Request)).Severity);
    }

    [Fact]
    public void Overlap_APendingRequestOverlappingAJobAlreadyTaken_IsDanger_AndOnlyThePendingOneIsFlagged()
    {
        var taken = Job(Request(Wed, "11:00", 120, ServiceRequestStatus.PresoInCarico)); // 11:00-13:00
        var pending = Job(Request(Wed, "10:00", 120)); // 10:00-12:00
        var input = Input([taken, pending]);

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(input, pending.Request));

        Assert.Equal(SupplierConflictCodes.Overlap, conflict.Code);
        Assert.Equal(SupplierConflictCodes.Danger, conflict.Severity);
        Assert.Equal(taken.Request.Id, conflict.OtherRequestId);
        Assert.Null(conflict.WindowId);
        // What waits for an answer never counts against a job already taken: the taken one is fine.
        Assert.Empty(SupplierConflictDetector.ConflictsOf(input, taken.Request));
        Assert.Equal([pending.Request.Id], SupplierConflictDetector.Detect(input, [taken.Request, pending.Request]).Select(item => item.RequestId));
    }

    [Fact]
    public void Overlap_TwoRequestsStillWaiting_EachCountsAgainstTheOther()
    {
        var first = Job(Request(Wed, "08:00", 120));
        var second = Job(Request(Wed, "09:00", 120));
        var input = Input([first, second]);

        var forFirst = Assert.Single(SupplierConflictDetector.ConflictsOf(input, first.Request));
        var forSecond = Assert.Single(SupplierConflictDetector.ConflictsOf(input, second.Request));

        Assert.Equal(second.Request.Id, forFirst.OtherRequestId);
        Assert.Equal(first.Request.Id, forSecond.OtherRequestId);
    }

    [Fact]
    public void Overlap_TwoJobsBothTaken_AreBothFlagged_TheApiCannotMakeThisButTheDataMayHaveIt()
    {
        var first = Job(Request(Wed, "08:00", 120, ServiceRequestStatus.PresoInCarico));
        var second = Job(Request(Wed, "09:00", 120, ServiceRequestStatus.InCorso));
        var input = Input([first, second]);

        Assert.Equal(second.Request.Id, Assert.Single(SupplierConflictDetector.ConflictsOf(input, first.Request)).OtherRequestId);
        Assert.Equal(first.Request.Id, Assert.Single(SupplierConflictDetector.ConflictsOf(input, second.Request)).OtherRequestId);
    }

    [Fact]
    public void Overlap_TheOtherThingIsAnEngagementOfTheCalendar_IsExternal_WithItsWindow()
    {
        var job = Job(Request(Wed, "10:00", 60)); // 10:00-11:00
        var windowId = Guid.NewGuid();
        var engagement = SupplierOccupancy.External(At(Wed, "10:30"), At(Wed, "11:30"), windowId);

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job], engagement), job.Request));

        Assert.Equal(SupplierConflictCodes.External, conflict.Code);
        Assert.Equal(SupplierConflictCodes.Danger, conflict.Severity);
        Assert.Equal(windowId, conflict.WindowId);
        Assert.Null(conflict.OtherRequestId);
    }

    [Fact]
    public void External_ADayClosedByTheCalendarFeed_IsAnotherEngagement_ADayClosedByHandIsADayOff()
    {
        var job = Job(Request(Wed, "10:00", 60));
        var closed = new HashSet<DateOnly> { D(Wed) };

        var byHand = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job], closedDays: closed), job.Request));
        var byTheFeed = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job], closedDays: closed, calendarClosedDays: closed), job.Request));

        Assert.Equal(SupplierConflictCodes.DayOff, byHand.Code);
        Assert.Equal(SupplierConflictCodes.Warning, byHand.Severity);
        Assert.Equal(SupplierConflictCodes.External, byTheFeed.Code);
        Assert.Equal(SupplierConflictCodes.Danger, byTheFeed.Severity);
        Assert.Null(byTheFeed.WindowId);
    }

    [Fact]
    public void Block_AJobOverlappingHoursTheSupplierBlocked_IsDanger_WithTheBlock()
    {
        var job = Job(Request(Wed, "10:00", 60));
        var windowId = Guid.NewGuid();
        var block = SupplierOccupancy.Block(At(Wed, "10:30"), At(Wed, "12:00"), windowId);

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job], block), job.Request));

        Assert.Equal(SupplierConflictCodes.Block, conflict.Code);
        Assert.Equal(SupplierConflictCodes.Danger, conflict.Severity);
        Assert.Equal(windowId, conflict.WindowId);
    }

    [Fact]
    public void DayOff_AWeekdayWithNoHoursAndNoExtraOpening_IsADayOff_AnExtraOpeningMakesItAWorkingDay()
    {
        var job = Job(Request(Sun, "10:00", 60)); // a rest day
        var opening = new SupplierInterval(At(Sun, "09:00"), At(Sun, "12:00"));

        var rest = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job]), job.Request));
        var opened = SupplierConflictDetector.ConflictsOf(Input([job], extra: [opening]), job.Request);

        Assert.Equal(SupplierConflictCodes.DayOff, rest.Code);
        Assert.Equal(SupplierConflictCodes.Warning, rest.Severity);
        Assert.Empty(opened);
    }

    [Theory]
    [InlineData("13:00", 60, true)] // in the lunch break
    [InlineData("12:30", 60, true)] // starts inside the morning, ends after it
    [InlineData("07:30", 60, true)] // starts before the day
    [InlineData("17:30", 60, true)] // ends after the evening
    [InlineData("12:00", 60, false)] // ends exactly when the morning ends: inside
    [InlineData("14:00", 240, false)] // the whole afternoon band
    [InlineData("08:00", 600, true)] // across the break: two bands are two bands
    public void OutsideHours_TheDayHasHoursButTheJobIsNotInsideOne(string start, int minutes, bool outside)
    {
        var job = Job(Request(Wed, start, minutes));
        string[] expected = outside ? [SupplierConflictCodes.OutsideHours] : [];

        Assert.Equal(expected, Codes(Input([job]), job));
    }

    [Fact]
    public void OutsideHours_AnExtraOpeningThatTouchesTheBands_JoinsThemIntoOne()
    {
        var job = Job(Request(Wed, "12:30", 120)); // 12:30-14:30, across the lunch break
        var lunch = new SupplierInterval(At(Wed, "13:00"), At(Wed, "14:00"));

        Assert.Equal([SupplierConflictCodes.OutsideHours], Codes(Input([job]), job));
        Assert.Empty(Codes(Input([job], extra: [lunch]), job));
    }

    [Fact]
    public void DayFull_TheDayAlreadyHoldsAsManyOtherJobsAsTheMaximum_IsAWarning()
    {
        var first = Job(Request(Wed, "08:00", 60, ServiceRequestStatus.PresoInCarico));
        var second = Job(Request(Wed, "10:00", 60, ServiceRequestStatus.PresoInCarico));
        var third = Job(Request(Wed, "15:00", 60, ServiceRequestStatus.PresoInCarico));
        var fourth = Job(Request(Wed, "16:30", 60)); // pending
        var input = Input([first, second, third, fourth]); // a maximum of 3 a day

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(input, fourth.Request));

        Assert.Equal(SupplierConflictCodes.DayFull, conflict.Code);
        Assert.Equal(SupplierConflictCodes.Warning, conflict.Severity);
        // The three that were taken are within the maximum: they do not count the one that waits.
        Assert.Empty(SupplierConflictDetector.ConflictsOf(input, first.Request));
        Assert.Empty(SupplierConflictDetector.ConflictsOf(input, third.Request));
    }

    [Fact]
    public void DayFull_ADayAboveTheMaximumWithJobsThatAllWait_FlagsEveryOneOfThem()
    {
        // As the planner sees it: with any one of them left out, the others already fill the day.
        var jobs = new[] { Job(Request(Wed, "08:00", 60)), Job(Request(Wed, "10:00", 60)), Job(Request(Wed, "15:00", 60)) };
        var input = Input(jobs, rules: Rules(maxJobsPerDay: 2));

        Assert.All(jobs, job => Assert.Equal([SupplierConflictCodes.DayFull], Codes(input, job)));
    }

    [Fact]
    public void DayFull_ABookingWaitingForItsEmailCheckCountsForTheDay_AnExpiredOneDoesNot()
    {
        var job = Job(Request(Wed, "15:00", 60));
        var live = SupplierOccupancy.Hold(At(Wed, "08:00"), At(Wed, "09:00"), Now.AddMinutes(30));
        var expired = SupplierOccupancy.Hold(At(Wed, "10:00"), At(Wed, "11:00"), Now.AddMinutes(-1));
        var rules = Rules(maxJobsPerDay: 1);

        Assert.Equal([SupplierConflictCodes.DayFull], Codes(Input([job], live, rules: rules), job));
        Assert.Empty(Codes(Input([job], expired, rules: rules), job));
    }

    [Fact]
    public void Buffer_CloserThanThePauseButNotOverlapping_IsABufferWarning_Exactly30MinutesIsFine()
    {
        var taken = Job(Request(Wed, "10:00", 60, ServiceRequestStatus.PresoInCarico)); // 10:00-11:00, pause of 30 minutes
        var near = Job(Request(Wed, "11:15", 60));
        var enough = Job(Request(Wed, "11:30", 60));

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([taken, near]), near.Request));
        Assert.Equal(SupplierConflictCodes.Buffer, conflict.Code);
        Assert.Equal(SupplierConflictCodes.Warning, conflict.Severity);
        Assert.Equal(taken.Request.Id, conflict.OtherRequestId);
        Assert.Empty(Codes(Input([taken, enough]), enough));
    }

    [Fact]
    public void Buffer_TheThingsThatTouchWithNoPause_AreNotAnOverlap_TheIntervalsAreHalfOpen()
    {
        var first = Job(Request(Wed, "10:00", 60, ServiceRequestStatus.PresoInCarico));
        var second = Job(Request(Wed, "11:00", 60));

        Assert.Empty(Codes(Input([first, second], rules: Rules(bufferMinutes: 0)), second));
        Assert.Equal([SupplierConflictCodes.Buffer], Codes(Input([first, second], rules: Rules(bufferMinutes: 30)), second));
    }

    [Fact]
    public void Buffer_AlsoAppliesToABlockAndAnEngagement_AsThePlannerKeepsThePauseFromEverything()
    {
        var job = Job(Request(Wed, "11:15", 60));
        var windowId = Guid.NewGuid();
        var block = SupplierOccupancy.Block(At(Wed, "10:00"), At(Wed, "11:00"), windowId);

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job], block), job.Request));

        Assert.Equal(SupplierConflictCodes.Buffer, conflict.Code);
        Assert.Equal(windowId, conflict.WindowId);
    }

    // ─── Severity, order, one per code ───────────────────────────────────────────

    [Fact]
    public void Conflicts_AreListedDangerFirst_ThenInTheOrderOfTheCodes()
    {
        // Sunday (a day off, warning), a block over it (danger), and a job taken just before it (buffer, warning).
        var job = Job(Request(Sun, "10:00", 60));
        var taken = Job(Request(Sun, "09:00", 45, ServiceRequestStatus.PresoInCarico)); // 09:00-09:45: 15 minutes before
        var block = SupplierOccupancy.Block(At(Sun, "10:30"), At(Sun, "11:30"), Guid.NewGuid());

        var codes = Codes(Input([job, taken], block), job);

        Assert.Equal([SupplierConflictCodes.Block, SupplierConflictCodes.DayOff, SupplierConflictCodes.Buffer], codes);
    }

    [Fact]
    public void Conflicts_AtMostOnePerCode_TheFirstThingInTheWayByStart()
    {
        var job = Job(Request(Wed, "10:00", 180)); // 10:00-13:00
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();
        var input = Input(
            [job],
            SupplierOccupancy.Block(At(Wed, "12:00"), At(Wed, "12:30"), late),
            SupplierOccupancy.Block(At(Wed, "10:30"), At(Wed, "11:00"), early));

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(input, job.Request));

        Assert.Equal(SupplierConflictCodes.Block, conflict.Code);
        Assert.Equal(early, conflict.WindowId);
    }

    [Fact]
    public void Overlap_WhenSeveralThingsAreInTheWay_TheJobAlreadyTakenIsTheOneNamed()
    {
        var pending = Job(Request(Wed, "10:00", 120)); // 10:00-12:00
        var other = Job(Request(Wed, "10:00", 60)); // pending too, and it starts first
        var taken = Job(Request(Wed, "11:00", 60, ServiceRequestStatus.PresoInCarico));

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([pending, other, taken]), pending.Request));

        Assert.Equal(taken.Request.Id, conflict.OtherRequestId);
    }

    [Fact]
    public void Codes_AreStable_AndEverySeverityIsOneOfTheTwo()
    {
        Assert.Equal(
            ["time_off", "overlap", "external", "block", "day_off", "outside_hours", "day_full", "buffer"],
            SupplierConflictCodes.All);
        Assert.All(SupplierConflictCodes.All, code => Assert.Contains(SupplierConflictCodes.SeverityOf(code), new[] { "danger", "warning" }));
        Assert.Equal(
            ["time_off", "overlap", "external", "block"],
            SupplierConflictCodes.All.Where(code => SupplierConflictCodes.SeverityOf(code) == "danger"));
    }

    // ─── What is not a conflict, and which jobs are looked at ────────────────────

    [Fact]
    public void NotAConflict_TheNotice_TheHorizon_TheGridOfTheSlots_AreRulesOfOfferingNotOfWhatIsAlreadyBooked()
    {
        // Tuesday 08:00 is inside the 24 hours of notice (now is Monday 09:00), 7 December is far beyond the 35 days of the horizon,
        // and 10:07 is not on the grid of the slots: none of them is a problem for a job that is already in the agenda.
        var soon = Job(Request(Tue, "08:00", 60));
        var far = Job(Request("2026-12-07", "10:00", 60));
        var offGrid = Job(Request(Wed, "10:07", 95));

        Assert.Empty(SupplierConflictDetector.Detect(Input([soon, far, offGrid]), [soon.Request, far.Request, offGrid.Request]));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public void Detect_ARequestThatIsNotAJobAnyMore_IsNotLookedAt(ServiceRequestStatus status)
    {
        var job = Job(Request(Sun, "10:00", 60, status)); // would be a day off

        Assert.Empty(SupplierConflictDetector.Detect(Input([job]), [job.Request]));
    }

    [Fact]
    public void Detect_AJobThatHasStartedIsNotLookedAt_OneThatStartsRightNowIs()
    {
        var job = Job(Request(Sun, "10:00", 60, ServiceRequestStatus.InCorso)); // a rest day

        var startingNow = Input([job], now: At(Sun, "10:00"));
        var startedASecondAgo = Input([job], now: At(Sun, "10:00").AddSeconds(1));

        Assert.Single(SupplierConflictDetector.Detect(startingNow, [job.Request]));
        Assert.Empty(SupplierConflictDetector.Detect(startedASecondAgo, [job.Request]));
    }

    [Fact]
    public void Detect_AJobWithAnEndThatIsNotAfterItsStart_IsNotLookedAt()
    {
        var broken = new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, At(Sun, "10:00"), At(Sun, "10:00"));

        Assert.Empty(SupplierConflictDetector.Detect(Input([]), [broken]));
    }

    [Fact]
    public void Detect_ARequestWithNoConflictIsNotListed_TheOthersComeByStartThenId()
    {
        var late = Job(Request(Sun, "15:00", 60));
        var early = Job(Request(Sun, "09:00", 60));
        var fine = Job(Request(Wed, "10:00", 60));

        var items = SupplierConflictDetector.Detect(Input([late, early, fine]), [late.Request, fine.Request, early.Request]);

        Assert.Equal([early.Request.Id, late.Request.Id], items.Select(item => item.RequestId));
        Assert.Equal(new DateOnly(2026, 10, 11), items[0].Date);
        Assert.Equal(early.Request.StartUtc, items[0].StartUtc);
    }

    [Fact]
    public void Detect_WhatIsInTheWayOfAJobIsOnlyWhatTheSupplierHasThatDay_AnotherDaysThingsAreNot()
    {
        var job = Job(Request(Wed, "10:00", 60));
        var tomorrow = SupplierOccupancy.Block(At(Thu, "10:00"), At(Thu, "11:00"), Guid.NewGuid());

        Assert.Empty(Codes(Input([job], tomorrow), job));
    }

    // ─── The suggestion ──────────────────────────────────────────────────────────

    [Fact]
    public void SuggestedSlot_ForARequestStillWaiting_IsTheFirstSlotThePlannerOffersOnceTheRequestIsOutOfItsOwnWay()
    {
        var taken = Job(Request(Wed, "08:00", 240, ServiceRequestStatus.PresoInCarico)); // 08:00-12:00
        var pending = Job(Request(Wed, "09:00", 120)); // 09:00-11:00, in the way of the job taken
        var input = Input([taken, pending]);

        var item = Assert.Single(SupplierConflictDetector.Detect(input, [taken.Request, pending.Request]));

        // Worked out the way PlanAsync(exceptRequestId) does it: the whole input but the request, from its day on, as long as it.
        var expected = SupplierSlotPlanner.PlanRange(D(Wed), D(Wed).AddDays(21), Input([taken]), new SupplierSlotQuery(120))
            .SelectMany(plan => plan.Slots)
            .First();
        Assert.Equal(pending.Request.Id, item.RequestId);
        Assert.Equal(expected, item.SuggestedSlot);
        Assert.Equal(TimeSpan.FromMinutes(120), item.SuggestedSlot!.Value.EndUtc - item.SuggestedSlot.Value.StartUtc);
        // The morning is taken (08:00-12:00 and the pause) and the band ends at 13:00: the first place is the afternoon.
        Assert.Equal(At(Wed, "14:00"), item.SuggestedSlot.Value.StartUtc);
    }

    [Fact]
    public void SuggestedSlot_ForAJobAlreadyTaken_IsNull_NoOtherTimeCanBeProposedForIt()
    {
        var taken = Job(Request(Sun, "10:00", 60, ServiceRequestStatus.PresoInCarico));

        var item = Assert.Single(SupplierConflictDetector.Detect(Input([taken]), [taken.Request]));

        Assert.Null(item.SuggestedSlot);
        Assert.Equal([SupplierConflictCodes.DayOff], item.Conflicts.Select(conflict => conflict.Code));
    }

    [Fact]
    public void SuggestedSlot_WhenNothingIsFreeSoon_IsNull_TheConflictIsStillReported()
    {
        var job = Job(Request(Wed, "10:00", 60));
        var input = Input([job], timeOff: [new SupplierDateRange(D(Wed), D(Wed).AddDays(40))]);

        var item = Assert.Single(SupplierConflictDetector.Detect(input, [job.Request]));

        Assert.Equal([SupplierConflictCodes.TimeOff], item.Conflicts.Select(conflict => conflict.Code));
        Assert.Null(item.SuggestedSlot);
    }

    [Fact]
    public void SuggestedSlot_IsFoundFromTheDayOfTheJobOn_NeverBefore()
    {
        // Wednesday is on leave: the first free place is Thursday at 08:00, nothing on Tuesday even though Tuesday is free.
        var job = Job(Request(Wed, "10:00", 60));
        var input = Input([job], timeOff: [new SupplierDateRange(D(Wed), D(Wed))], rules: Rules(noticeHours: 0));

        var item = Assert.Single(SupplierConflictDetector.Detect(input, [job.Request]));

        Assert.Equal(At(Thu, "08:00"), item.SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public void SuggestedSlot_TheRequestIsOutOfItsOwnWay_ItsOwnPauseDoesNotHideTheFirstSlotOfTheDay()
    {
        // 07:00-08:00 is before the hours (08:00), so the request is outside them. Left in the input, its own pause (until 08:30)
        // would hide the 08:00 slot and the suggestion would be 09:00; as PlanAsync(exceptRequestId) does, it is left out: 08:00.
        var job = Job(Request(Wed, "07:00", 60));

        var item = Assert.Single(SupplierConflictDetector.Detect(Input([job]), [job.Request]));

        Assert.Equal([SupplierConflictCodes.OutsideHours], item.Conflicts.Select(conflict => conflict.Code));
        Assert.Equal(At(Wed, "08:00"), item.SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public void SuggestedSlot_FollowsTheWeekdaysOfTheServiceOfTheRequest()
    {
        // Two requests on a Sunday (a day off): one for a service offered on Fridays only, one for any day.
        var onlyFriday = SupplierServiceWeekdays.ToMask([DayOfWeek.Friday]);
        var forFridays = Job(new SupplierConflictRequest(
            Guid.NewGuid(), ServiceRequestStatus.Richiesto, At(Sun, "10:00"), At(Sun, "11:00"), ServiceMinNoticeHours: 0, ServiceWeekdaysMask: onlyFriday));
        var forAnyDay = Job(new SupplierConflictRequest(
            Guid.NewGuid(), ServiceRequestStatus.Richiesto, At(Sun, "12:00"), At(Sun, "13:00"), ServiceMinNoticeHours: 0));

        var items = SupplierConflictDetector.Detect(Input([forFridays, forAnyDay], rules: Rules(noticeHours: 0)), [forFridays.Request, forAnyDay.Request]);

        Assert.Equal(At("2026-10-16", "08:00"), items.Single(item => item.RequestId == forFridays.Request.Id).SuggestedSlot!.Value.StartUtc);
        Assert.Equal(At("2026-10-12", "08:00"), items.Single(item => item.RequestId == forAnyDay.Request.Id).SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public void SuggestedSlot_TheNoticeOfTheServiceDecidesTheEarliestStart()
    {
        // Now is Monday 09:00. A job on Tuesday over a block, for a service that asks 72 hours of notice: nothing before Thursday 09:00.
        var job = Job(new SupplierConflictRequest(
            Guid.NewGuid(), ServiceRequestStatus.Richiesto, At(Tue, "10:00"), At(Tue, "11:00"), ServiceMinNoticeHours: 72));
        var block = SupplierOccupancy.Block(At(Tue, "10:00"), At(Tue, "11:00"), Guid.NewGuid());

        var item = Assert.Single(SupplierConflictDetector.Detect(Input([job], block), [job.Request]));

        Assert.Equal(At(Thu, "09:00"), item.SuggestedSlot!.Value.StartUtc);
    }

    // ─── Daylight saving time ────────────────────────────────────────────────────

    [Fact]
    public void Dst_TheDayTheClockGoesForward_AJobAcrossTheSkippedHourIsAsLongAsTheTimeThatReallyPasses()
    {
        // 29 March 2026, hours 00:00-06:00 on the wall clock = 23:00Z (28th) to 04:00Z, five real hours.
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 0, 6 * 60) };
        var inside = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, Utc("2026-03-29T00:30:00Z"), Utc("2026-03-29T01:30:00Z")));
        var pastTheEnd = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, Utc("2026-03-29T03:30:00Z"), Utc("2026-03-29T04:30:00Z")));
        var input = Input([inside, pastTheEnd], now: Utc("2026-03-25T07:00:00Z"), hours: week);

        Assert.Empty(Codes(input, inside)); // 01:30 CET to 03:30 CEST: an hour that straddles the change, inside the band
        Assert.Equal([SupplierConflictCodes.OutsideHours], Codes(input, pastTheEnd));
    }

    [Fact]
    public void Dst_TheDayTheClockGoesForward_ABlockInTheHourAfterTheChange_IsFoundByInstants()
    {
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 0, 6 * 60) };
        var job = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, Utc("2026-03-29T00:30:00Z"), Utc("2026-03-29T01:30:00Z")));
        var blockId = Guid.NewGuid();
        var block = SupplierOccupancy.Block(Utc("2026-03-29T01:00:00Z"), Utc("2026-03-29T02:00:00Z"), blockId); // 03:00-04:00 CEST

        var conflict = Assert.Single(SupplierConflictDetector.ConflictsOf(Input([job], block, now: Utc("2026-03-25T07:00:00Z"), hours: week), job.Request));

        Assert.Equal(SupplierConflictCodes.Block, conflict.Code);
        Assert.Equal(blockId, conflict.WindowId);
    }

    [Fact]
    public void Dst_TheDayTheClockGoesBack_TheTwoPassesOfTheRepeatedHourAreTwoDifferentHours()
    {
        // 25 October 2026: 02:00-03:00 happens twice (00:00Z-01:00Z in summer time, then 01:00Z-02:00Z). Back to back is no overlap.
        var week = new[] { new SupplierWeeklyBand(DayOfWeek.Sunday, 0, 6 * 60) };
        var rules = Rules(bufferMinutes: 0, noticeHours: 0);
        var first = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.PresoInCarico, Utc("2026-10-25T00:00:00Z"), Utc("2026-10-25T01:00:00Z")));
        var second = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, Utc("2026-10-25T01:00:00Z"), Utc("2026-10-25T02:00:00Z")));
        var across = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, Utc("2026-10-25T00:30:00Z"), Utc("2026-10-25T01:30:00Z")));
        var now = Utc("2026-10-20T07:00:00Z");

        Assert.Empty(Codes(Input([first, second], now: now, hours: week, rules: rules), second));

        var overlapping = Input([first, second, across], now: now, hours: week, rules: rules);
        Assert.Equal([SupplierConflictCodes.Overlap], Codes(overlapping, across));
        Assert.Equal(first.Request.Id, SupplierConflictDetector.ConflictsOf(overlapping, across.Request)[0].OtherRequestId);
    }

    [Theory]
    [InlineData("2026-10-24T22:30:00Z", "2026-10-25")] // 00:30 CEST on the 25th: the day of the job is the 25th, not the 24th
    [InlineData("2026-03-28T23:30:00Z", "2026-03-29")] // 00:30 CET on the 29th
    public void Dst_TheDayOfAJobIsTheDayInRome_NotTheDayInUtc(string start, string expectedDay)
    {
        var week = Enum.GetValues<DayOfWeek>().Select(day => new SupplierWeeklyBand(day, 0, 2 * 60)).ToArray();
        var job = Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, Utc(start), Utc(start).AddMinutes(30)));
        var timeOff = new SupplierDateRange(D(expectedDay), D(expectedDay));

        var item = Assert.Single(SupplierConflictDetector.Detect(Input([job], now: Utc(start).AddDays(-3), hours: week, timeOff: [timeOff]), [job.Request]));

        Assert.Equal(D(expectedDay), item.Date);
        Assert.Equal([SupplierConflictCodes.TimeOff], item.Conflicts.Select(conflict => conflict.Code));
    }

    // ─── The planner and the detector read the agenda the same way ───────────────

    [Fact]
    public void Equivalence_ForARequestStillWaiting_TheDetectorFlagsExactlyTheTimesThePlannerDoesNotOffer()
    {
        // Every half hour of the week (and of the two days the clock changes), a request of one hour left out of its own way: the
        // planner offers the time exactly when the detector finds nothing. Notice 0 and a step of 30 minutes, so that nothing but
        // the agenda decides (the notice and the horizon are rules of offering, not of what is booked).
        var mismatches = new List<string>();
        var checkedTimes = 0;
        foreach (var scenario in EquivalenceScenarios())
        {
            foreach (var day in scenario.Days)
            {
                foreach (var start in HalfHoursOf(day))
                {
                    checkedTimes++;
                    var request = new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.Richiesto, start, start.AddMinutes(60));
                    var withRequest = scenario.Input with
                    {
                        Occupancies = [.. scenario.Input.Occupancies, SupplierOccupancy.TimedRequest(request.StartUtc, request.EndUtc, request.Id, committed: false)],
                    };

                    var offered = SupplierSlotPlanner.PlanDay(day, scenario.Input, new SupplierSlotQuery(60, MinNoticeHours: 0))
                        .Slots.Any(slot => slot.StartUtc == start);
                    var conflicts = SupplierConflictDetector.ConflictsOf(withRequest, request);

                    if (offered == (conflicts.Count > 0))
                        mismatches.Add($"{scenario.Name}: {start:O} offered={offered} conflicts=[{string.Join(",", conflicts.Select(c => c.Code))}]");
                }
            }
        }

        Assert.True(checkedTimes > 4000, $"the property looked at too few times ({checkedTimes}): the scenarios are stale");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} times where the planner and the detector disagree, first: {string.Join(" | ", mismatches.Take(8))}");
    }

    [Fact]
    public void Equivalence_ForAJobAlreadyTaken_TheSameHoldsWhenNothingWaitsForAnAnswer()
    {
        // A job taken is not in the way of anything that waits, so where nothing waits the detector and the planner agree for it too.
        var mismatches = new List<string>();
        var checkedTimes = 0;
        foreach (var scenario in EquivalenceScenarios().Where(scenario => !scenario.HasWaiting))
        {
            foreach (var day in scenario.Days)
            {
                foreach (var start in HalfHoursOf(day))
                {
                    checkedTimes++;
                    var request = new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.PresoInCarico, start, start.AddMinutes(60));
                    var withRequest = scenario.Input with
                    {
                        Occupancies = [.. scenario.Input.Occupancies, SupplierOccupancy.TimedRequest(request.StartUtc, request.EndUtc, request.Id, committed: true)],
                    };

                    var offered = SupplierSlotPlanner.PlanDay(day, scenario.Input, new SupplierSlotQuery(60, MinNoticeHours: 0))
                        .Slots.Any(slot => slot.StartUtc == start);

                    if (offered == (SupplierConflictDetector.ConflictsOf(withRequest, request).Count > 0))
                        mismatches.Add($"{scenario.Name}: {start:O} offered={offered}");
                }
            }
        }

        Assert.True(checkedTimes > 2000, $"the property looked at too few times ({checkedTimes}): the scenarios are stale");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} disagreements, first: {string.Join(" | ", mismatches.Take(8))}");
    }

    /// <summary>A scenario of the property: an agenda, the days to try and whether something in it still waits for an answer.</summary>
    private sealed record Scenario(string Name, SupplierPlanningInput Input, IReadOnlyList<DateOnly> Days, bool HasWaiting);

    /// <summary>Every real half hour of a Europe/Rome day (46, 48 or 50 of them: the days the clock changes are shorter or longer).</summary>
    private static IEnumerable<DateTime> HalfHoursOf(DateOnly day)
    {
        for (var start = RomeCalendar.StartOfDayUtc(day); start < RomeCalendar.StartOfDayUtc(day.AddDays(1)); start += TimeSpan.FromMinutes(30))
            yield return start;
    }

    private static IEnumerable<Scenario> EquivalenceScenarios()
    {
        var week = Days(Tue, 5); // Tuesday to Saturday
        var weekPlusRest = Days(Tue, 6); // and the Sunday, a rest day
        var noPause = Rules(bufferMinutes: 0, noticeHours: 0, stepMinutes: 30);
        var pause = Rules(bufferMinutes: 30, noticeHours: 0, stepMinutes: 30);
        var longPause = Rules(bufferMinutes: 45, noticeHours: 0, stepMinutes: 30);
        var small = Rules(bufferMinutes: 30, maxJobsPerDay: 2, noticeHours: 0, stepMinutes: 30);

        yield return new("an empty agenda", Input([], rules: pause), weekPlusRest, HasWaiting: false);
        yield return new("a block on Wednesday morning", Input([], SupplierOccupancy.Block(At(Wed, "10:00"), At(Wed, "11:00")), rules: pause), week, HasWaiting: false);
        yield return new("a block, no pause", Input([], SupplierOccupancy.Block(At(Wed, "10:00"), At(Wed, "11:00")), rules: noPause), week, HasWaiting: false);
        yield return new("an engagement and a long pause", Input([], SupplierOccupancy.External(At(Fri, "15:00"), At(Fri, "16:30")), rules: longPause), week, HasWaiting: false);
        yield return new("a job taken", Input([Job(Request(Thu, "14:00", 120, ServiceRequestStatus.PresoInCarico))], rules: pause), week, HasWaiting: false);
        yield return new("a job in progress, no pause", Input([Job(Request(Thu, "09:00", 90, ServiceRequestStatus.InCorso))], rules: noPause), week, HasWaiting: false);
        yield return new("a job waiting", Input([Job(Request(Thu, "09:00", 60))], rules: pause), week, HasWaiting: true);
        yield return new(
            "a job taken and one waiting on the same day",
            Input([Job(Request(Thu, "14:00", 120, ServiceRequestStatus.PresoInCarico)), Job(Request(Thu, "09:00", 60))], rules: pause),
            week,
            HasWaiting: true);
        yield return new("a day off by hand", Input([], closedDays: new HashSet<DateOnly> { D(Wed) }, rules: pause), week, HasWaiting: false);
        yield return new(
            "a day closed by the feed",
            Input([], closedDays: new HashSet<DateOnly> { D(Wed) }, calendarClosedDays: new HashSet<DateOnly> { D(Wed) }, rules: pause),
            week,
            HasWaiting: false);
        yield return new("a time off over two days", Input([], timeOff: [new SupplierDateRange(D(Thu), D(Fri))], rules: pause), week, HasWaiting: false);
        yield return new(
            "an extra opening on Sunday and one over the lunch break",
            Input([], extra: [new SupplierInterval(At(Sun, "09:00"), At(Sun, "12:00")), new SupplierInterval(At(Wed, "13:00"), At(Wed, "14:00"))], rules: pause),
            weekPlusRest,
            HasWaiting: false);
        yield return new(
            "the maximum of two a day, two taken on Wednesday",
            Input([Job(Request(Wed, "08:00", 60, ServiceRequestStatus.PresoInCarico)), Job(Request(Wed, "15:00", 60, ServiceRequestStatus.PresoInCarico))], rules: small),
            week,
            HasWaiting: false);
        yield return new(
            "the maximum of two a day, one taken and one waiting",
            Input([Job(Request(Wed, "08:00", 60, ServiceRequestStatus.PresoInCarico)), Job(Request(Wed, "15:00", 60))], rules: small),
            week,
            HasWaiting: true);
        yield return new(
            "a live hold and an expired one",
            Input([], SupplierOccupancy.Hold(At(Thu, "10:00"), At(Thu, "11:00"), Now.AddMinutes(30)), SupplierOccupancy.Hold(At(Thu, "15:00"), At(Thu, "16:00"), Now.AddMinutes(-5)), rules: pause),
            week,
            HasWaiting: true);
        yield return new(
            "two jobs at once with a capacity of two",
            Input(
                [Job(Request(Fri, "09:00", 120, ServiceRequestStatus.PresoInCarico)), Job(Request(Fri, "10:00", 120, ServiceRequestStatus.PresoInCarico))],
                rules: Rules(bufferMinutes: 0, noticeHours: 0, stepMinutes: 30, parallelJobs: 2, maxJobsPerDay: 6)),
            week,
            HasWaiting: false);
        yield return new(
            "everything at once",
            Input(
                [Job(Request(Wed, "09:00", 60, ServiceRequestStatus.PresoInCarico)), Job(Request(Wed, "15:00", 90)), Job(Request(Thu, "10:00", 120, ServiceRequestStatus.InCorso))],
                SupplierOccupancy.Block(At(Wed, "11:00"), At(Wed, "12:00")),
                SupplierOccupancy.External(At(Thu, "15:00"), At(Thu, "17:00")),
                timeOff: [new SupplierDateRange(D(Fri), D(Fri))],
                rules: pause),
            week,
            HasWaiting: true);
        yield return new("waiting jobs side by side, no pause", Input([Job(Request(Fri, "10:00", 60)), Job(Request(Fri, "11:00", 60))], rules: noPause), week, HasWaiting: true);

        // The two days a year the clock changes: hours at night as well as by day, every real half hour of the day.
        var longDay = Enum.GetValues<DayOfWeek>()
            .SelectMany(day => new[] { new SupplierWeeklyBand(day, 0, 6 * 60), new SupplierWeeklyBand(day, 8 * 60, 20 * 60) })
            .ToArray();
        yield return new(
            "29 March, a block just after the change",
            Input([], SupplierOccupancy.Block(Utc("2026-03-29T01:00:00Z"), Utc("2026-03-29T02:00:00Z")), now: Utc("2026-03-25T07:00:00Z"), hours: longDay, rules: pause),
            [new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 30)],
            HasWaiting: false);
        yield return new(
            "25 October, a job in each pass of the repeated hour",
            Input(
                [
                    Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.PresoInCarico, Utc("2026-10-25T00:00:00Z"), Utc("2026-10-25T01:00:00Z"))),
                    Job(new SupplierConflictRequest(Guid.NewGuid(), ServiceRequestStatus.PresoInCarico, Utc("2026-10-25T02:00:00Z"), Utc("2026-10-25T03:00:00Z"))),
                ],
                now: Utc("2026-10-20T07:00:00Z"),
                hours: longDay,
                rules: pause),
            [new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26)],
            HasWaiting: false);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static DateOnly D(string day) => DateOnly.Parse(day, CultureInfo.InvariantCulture);

    private static IReadOnlyList<DateOnly> Days(string first, int count) => [.. Enumerable.Range(0, count).Select(i => D(first).AddDays(i))];

    /// <summary>The wall clock of Rome on a day, as the UTC instant it is.</summary>
    private static DateTime At(string day, string time) => RomeCalendar.ToUtc(D(day), TimeOnly.Parse(time, CultureInfo.InvariantCulture));

    private static SupplierConflictRequest Request(string day, string time, int minutes, ServiceRequestStatus status = ServiceRequestStatus.Richiesto) =>
        new(Guid.NewGuid(), status, At(day, time), At(day, time).AddMinutes(minutes));

    /// <summary>A request and the occupancy it is in the input of the planner (with its id and whether the supplier took it).</summary>
    private sealed record Booked(SupplierConflictRequest Request)
    {
        public SupplierOccupancy Occupancy => SupplierOccupancy.TimedRequest(Request.StartUtc, Request.EndUtc, Request.Id, Request.IsCommitted);
    }

    private static Booked Job(SupplierConflictRequest request) => new(request);

    private static SupplierPlanningRules Rules(
        int bufferMinutes = 30,
        int maxJobsPerDay = 3,
        int noticeHours = 24,
        int horizonDays = 35,
        int stepMinutes = 60,
        int parallelJobs = 1) =>
        new(bufferMinutes, maxJobsPerDay, noticeHours, horizonDays, stepMinutes, parallelJobs);

    private static SupplierPlanningInput Input(
        IEnumerable<Booked> jobs,
        SupplierOccupancy? first = null,
        SupplierOccupancy? second = null,
        DateTime? now = null,
        SupplierPlanningRules? rules = null,
        IReadOnlyList<SupplierWeeklyBand>? hours = null,
        IReadOnlyList<SupplierDateRange>? timeOff = null,
        IReadOnlySet<DateOnly>? closedDays = null,
        IReadOnlySet<DateOnly>? calendarClosedDays = null,
        IReadOnlyList<SupplierInterval>? extra = null)
    {
        var occupancies = jobs.Select(job => job.Occupancy).ToList();
        foreach (var more in new[] { first, second })
        {
            if (more is not null)
                occupancies.Add(more);
        }

        return new SupplierPlanningInput(
            now ?? Now,
            rules ?? Rules(),
            hours ?? DemoWeek,
            timeOff ?? [],
            closedDays ?? new HashSet<DateOnly>(),
            extra ?? [],
            occupancies,
            calendarClosedDays);
    }

    private static List<string> Codes(SupplierPlanningInput input, Booked job) =>
        SupplierConflictDetector.ConflictsOf(input, job.Request).Select(conflict => conflict.Code).ToList();
}
