using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02 (decisions D16 and D19): the rules of the scheduled change of rental mode without a database. One rule for both
/// directions (a stay, a block or a lease stands in the way of a day when its last day is on or after it, the property is
/// free from the day after), the first day is tomorrow, drafts are deleted and not waited for. "Today" is 10 October 2026.
/// </summary>
public class PropertyModeRulesTests
{
    private static readonly DateTime Today = Day(10);
    private static readonly Guid PropertyId = Guid.NewGuid();

    // ─── Constants and stored values ────────────────────────────────────────────────────────────────

    [Fact]
    public void StoredValues_AreTheIntegersOfTheColumnsAndAppendOnly()
    {
        // PropertyModeChanges.Status is an integer and the partial unique index is written with Scheduled = 0.
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<PropertyModeChangeStatus>().Select(v => (int)v).Order());
        Assert.Equal(0, (int)PropertyModeChangeStatus.Scheduled);
        Assert.Equal(1, (int)PropertyModeChangeStatus.Applied);
        Assert.Equal(2, (int)PropertyModeChangeStatus.Cancelled);
        Assert.Equal(3, (int)PropertyModeChangeStatus.Failed);
        // CalendarBlocks.ManualReason: the three the host chooses keep their numbers, ModeChange is appended.
        Assert.Equal(0, (int)CalendarBlockReason.Owner);
        Assert.Equal(1, (int)CalendarBlockReason.Maintenance);
        Assert.Equal(2, (int)CalendarBlockReason.Other);
        Assert.Equal(3, (int)CalendarBlockReason.ModeChange);
    }

    [Fact]
    public void ErrorCodes_AreThePublicContractOfTheApi()
    {
        Assert.Equal("property_mode_unchanged", PropertyModeErrorCodes.AlreadyInMode);
        Assert.Equal("property_mode_date_too_early", PropertyModeErrorCodes.DateTooEarly);
        Assert.Equal("property_mode_date_too_far", PropertyModeErrorCodes.DateTooFar);
        Assert.Equal("property_mode_blocked_by_bookings", PropertyModeErrorCodes.BlockedByBookings);
        Assert.Equal("property_mode_blocked_by_lease", PropertyModeErrorCodes.BlockedByLease);
        Assert.Equal("property_mode_blocked_by_draft_lease", PropertyModeErrorCodes.BlockedByDraftLease);
        Assert.Equal("property_mode_change_exists", PropertyModeErrorCodes.ChangeExists);
        Assert.Equal("property_mode_change_not_found", PropertyModeErrorCodes.ChangeNotFound);
        Assert.Equal("property_mode_change_not_scheduled", PropertyModeErrorCodes.ChangeNotScheduled);
        Assert.Equal("property_mode_target_invalid", PropertyModeErrorCodes.TargetInvalid);
        Assert.Equal("property_mode_changed", PropertyModeErrorCodes.PropertyChanged);
        Assert.Equal("PropertyModeBlockedByLease", PropertyModeErrorCodes.BlockedByLeaseMessageKey);
        Assert.Equal("PropertyModeBlockedByBookings", PropertyModeErrorCodes.BlockedByBookingsMessageKey);
        Assert.Equal("PropertyModeDateTooEarly", PropertyModeErrorCodes.DateTooEarlyMessageKey);
        Assert.Equal("PropertyModeChangeExists", PropertyModeErrorCodes.ChangeExistsMessageKey);
    }

    [Fact]
    public void FirstPossibleDay_IsTomorrow_AndTheLastIsTenYearsAhead()
    {
        Assert.Equal(Day(11), PropertyModeRules.FirstPossibleDay(Today));
        Assert.Equal(Day(11), PropertyModeRules.FirstPossibleDay(Today.AddHours(15)));
        Assert.Equal(new DateTime(2036, 10, 10, 0, 0, 0, DateTimeKind.Utc), PropertyModeRules.LastPossibleDay(Today));
    }

    [Fact]
    public void CalendarBlockEnd_IsTwoYearsAfterTheDayOfTheChange()
    {
        Assert.Equal(2, PropertyModeRules.CalendarBlockYears);
        Assert.Equal(new DateTime(2028, 12, 1, 0, 0, 0, DateTimeKind.Utc), PropertyModeRules.CalendarBlockEnd(new DateTime(2026, 12, 1)));
        // From a 29 February the block ends on the 28th (AddYears), never on a day that does not exist.
        Assert.Equal(new DateTime(2030, 2, 28, 0, 0, 0, DateTimeKind.Utc), PropertyModeRules.CalendarBlockEnd(new DateTime(2028, 2, 29)));
    }

    [Theory]
    [InlineData(RentalMode.Short, RentalMode.Long)]
    [InlineData(RentalMode.Long, RentalMode.Short)]
    public void Opposite_IsTheOtherMode(RentalMode mode, RentalMode expected)
    {
        Assert.Equal(expected, PropertyModeRules.Opposite(mode));
    }

    // ─── Earliest day ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EarliestDate_NothingInTheWay_IsTomorrow()
    {
        Assert.Equal(Day(11), PropertyModeRules.EarliestDate(Today, []));
    }

    [Fact]
    public void EarliestDate_AStayThatLeavesLater_IsTheDayAfterItsDeparture()
    {
        // The last guest leaves on the 20th: the property can change mode from the 21st ("last departure + 1").
        var candidates = new[] { Stay(Day(12), Day(15)), Stay(Day(18), Day(20)) };

        Assert.Equal(Day(21), PropertyModeRules.EarliestDate(Today, candidates));
    }

    [Fact]
    public void EarliestDate_SeveralKinds_FollowsTheLatestOfThemAll()
    {
        var candidates = new[] { Stay(Day(12), Day(15)), Block(Day(14), Day(25)), Lease(Day(1), Day(22)) };

        Assert.Equal(Day(26), PropertyModeRules.EarliestDate(Today, candidates));
    }

    [Fact]
    public void EarliestDate_AStayThatLeavesTomorrow_IsTheDayAfterTomorrow()
    {
        Assert.Equal(Day(12), PropertyModeRules.EarliestDate(Today, [Stay(Day(9), Day(11))]));
    }

    [Fact]
    public void EarliestDate_SomethingAlreadyFreeBeforeTomorrow_DoesNotPushTheDate()
    {
        // Left today: free from tomorrow, which is the first day anyway.
        Assert.Equal(Day(11), PropertyModeRules.EarliestDate(Today, [Stay(Day(8), Day(10))]));
    }

    [Fact]
    public void EarliestDate_DraftLeases_NoDateFreesThemSoTheyDoNotMoveIt()
    {
        Assert.Equal(Day(11), PropertyModeRules.EarliestDate(Today, [Draft(Day(1), Day(400))]));
    }

    // ─── What stands in the way of a day ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(20, true)]  // the stay leaves on the 20th: the 20th is still in its way
    [InlineData(21, false)] // free from the 21st
    [InlineData(25, false)]
    [InlineData(11, true)]
    public void BlockersFor_AStayThatLeavesOnThe20th_StandsInTheWayUntilTheDayAfterItsDeparture(int day, bool inTheWay)
    {
        var stay = Stay(Day(12), Day(20));

        var blockers = PropertyModeRules.BlockersFor([stay], Day(day));

        Assert.Equal(inTheWay, blockers.Count == 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(900)]
    public void BlockersFor_ADraftLease_StandsInTheWayOfEveryDay(int dayOffset)
    {
        var draft = Draft(Day(12), Day(15));

        Assert.Single(PropertyModeRules.BlockersFor([draft], Today.AddDays(dayOffset)));
    }

    [Fact]
    public void BlockersFor_OrdersByStartThenKindThenId_AndKeepsOnlyWhatIsInTheWay()
    {
        var late = Stay(Day(15), Day(30));
        var early = Stay(Day(11), Day(30));
        var free = Stay(Day(11), Day(12));

        var blockers = PropertyModeRules.BlockersFor([late, free, early], Day(20));

        Assert.Equal([early.Id, late.Id], blockers.Select(b => b.Id));
    }

    // ─── Assess: the answer of the preview ──────────────────────────────────────────────────────────

    [Fact]
    public void Assess_ToLongWithoutADay_EvaluatesTheEarliestDayAndCanBeScheduled()
    {
        var preview = AssessToLong(requested: null, [Stay(Day(12), Day(20))]);

        Assert.Equal(Day(21), preview.EarliestDate);
        Assert.Equal(Day(21), preview.Date);
        Assert.Empty(preview.Blockers);
        Assert.Empty(preview.Issues);
        Assert.True(preview.CanSchedule);
        Assert.Equal((RentalMode.Short, RentalMode.Long), (preview.From, preview.To));
        Assert.Equal(Today, preview.Today);
        Assert.Equal(PropertyId, preview.PropertyId);
    }

    [Fact]
    public void Assess_ToLongOnADayBeforeTheLastDeparture_ListsTheStaysInTheWayAndIsBlockedByBookings()
    {
        var inTheWay = Stay(Day(18), Day(25));
        var imported = Block(Day(14), Day(22));
        var free = Stay(Day(11), Day(13));

        var preview = AssessToLong(Day(20), [free, inTheWay, imported]);

        Assert.Equal([PropertyModeErrorCodes.BlockedByBookings], preview.Issues);
        Assert.False(preview.CanSchedule);
        Assert.Equal([imported.Id, inTheWay.Id], preview.Blockers.Select(b => b.Id));
        Assert.Equal(Day(26), preview.EarliestDate);
        Assert.Equal(Day(20), preview.Date);
    }

    [Fact]
    public void Assess_ToLongOnTheEarliestDay_CanBeScheduled()
    {
        var preview = AssessToLong(Day(26), [Stay(Day(18), Day(25))]);

        Assert.True(preview.CanSchedule);
        Assert.Empty(preview.Blockers);
    }

    [Fact]
    public void Assess_ToShortWhileALeaseRuns_IsBlockedByLeaseAndTheEarliestDayIsTheDayAfterItsEnd()
    {
        var lease = Lease(Day(-100), Day(60));

        var preview = Assess(RentalMode.Long, RentalMode.Short, Day(30), [lease]);

        Assert.Equal([PropertyModeErrorCodes.BlockedByLease], preview.Issues);
        Assert.Equal(Day(61), preview.EarliestDate);
        Assert.Equal(PropertyModeBlockerKind.Lease, Assert.Single(preview.Blockers).Kind);
        Assert.True(Assess(RentalMode.Long, RentalMode.Short, Day(61), [lease]).CanSchedule);
        Assert.False(Assess(RentalMode.Long, RentalMode.Short, Day(60), [lease]).CanSchedule);
    }

    [Fact]
    public void Assess_ToShortWithADraft_IsBlockedByDraftLeaseOnEveryDayAndItsDatesDoNotMoveTheEarliestDay()
    {
        var draft = Draft(Day(100), Day(465));

        var preview = Assess(RentalMode.Long, RentalMode.Short, requested: null, [draft]);

        Assert.Equal(Day(11), preview.EarliestDate);
        Assert.Equal([PropertyModeErrorCodes.BlockedByDraftLease], preview.Issues);
        Assert.Equal(PropertyModeBlockerKind.DraftLease, Assert.Single(preview.Blockers).Kind);
        Assert.Null(preview.Blockers[0].FreeFrom);
        Assert.Contains(PropertyModeErrorCodes.BlockedByDraftLease, Assess(RentalMode.Long, RentalMode.Short, Day(900), [draft]).Issues);
    }

    [Fact]
    public void Assess_ToShortWithALeaseAndADraft_ReportsBoth()
    {
        var preview = Assess(RentalMode.Long, RentalMode.Short, Day(20), [Lease(Day(-10), Day(40)), Draft(Day(50), Day(80))]);

        Assert.Equal([PropertyModeErrorCodes.BlockedByLease, PropertyModeErrorCodes.BlockedByDraftLease], preview.Issues);
        Assert.Equal(2, preview.Blockers.Count);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    [InlineData(-300)]
    public void Assess_TodayOrThePast_IsDateTooEarlyAndWhatIsInTheWayIsEvaluatedForTomorrow(int day)
    {
        var stay = Stay(Day(9), Day(14));

        var preview = AssessToLong(Day(day), [stay]);

        Assert.Equal(PropertyModeErrorCodes.DateTooEarly, preview.Issues[0]);
        Assert.Contains(PropertyModeErrorCodes.BlockedByBookings, preview.Issues);
        Assert.Equal(stay.Id, Assert.Single(preview.Blockers).Id);
        Assert.Equal(Day(15), preview.EarliestDate);
    }

    [Fact]
    public void Assess_TomorrowWithNothingInTheWay_IsTheFirstDayThatCanBeScheduled()
    {
        Assert.True(AssessToLong(Day(11), []).CanSchedule);
    }

    [Fact]
    public void Assess_MoreThanTenYearsAhead_IsDateTooFar()
    {
        Assert.Equal([PropertyModeErrorCodes.DateTooFar], AssessToLong(Day(10).AddYears(10).AddDays(1), []).Issues);
        Assert.True(AssessToLong(Day(10).AddYears(10), []).CanSchedule);
    }

    [Fact]
    public void Assess_AChangeAlreadyWaits_ChangeExistsComesFirstAndTheChangeIsReturned()
    {
        var scheduled = new PropertyModeChange { PropertyId = PropertyId, Status = PropertyModeChangeStatus.Scheduled, EffectiveDate = Day(30) };

        var preview = PropertyModeRules.Assess(
            PropertyId, RentalMode.Short, RentalMode.Long, Today, Day(20), [Stay(Day(12), Day(25))], scheduled);

        Assert.Equal([PropertyModeErrorCodes.ChangeExists, PropertyModeErrorCodes.BlockedByBookings], preview.Issues);
        Assert.Same(scheduled, preview.ScheduledChange);
    }

    [Theory]
    [InlineData(PropertyModeChangeStatus.Applied)]
    [InlineData(PropertyModeChangeStatus.Cancelled)]
    [InlineData(PropertyModeChangeStatus.Failed)]
    public void Assess_AChangeThatIsOver_IsNotInTheWayNorReturned(PropertyModeChangeStatus status)
    {
        var over = new PropertyModeChange { PropertyId = PropertyId, Status = status, EffectiveDate = Day(-5) };

        var preview = PropertyModeRules.Assess(PropertyId, RentalMode.Short, RentalMode.Long, Today, Day(20), [], over);

        Assert.True(preview.CanSchedule);
        Assert.Null(preview.ScheduledChange);
    }

    // ─── EnsureAllowed: the errors of the creation ──────────────────────────────────────────────────

    [Fact]
    public void EnsureAllowed_NoIssues_DoesNotThrow()
    {
        PropertyModeRules.EnsureAllowed(AssessToLong(Day(20), []));
    }

    [Fact]
    public void EnsureAllowed_BlockedByBookings_Is409WithTheFirstFreeDay()
    {
        var error = Assert.Throws<DomainConflictException>(
            () => PropertyModeRules.EnsureAllowed(AssessToLong(Day(15), [Stay(Day(12), Day(20))])));

        Assert.Equal("property_mode_blocked_by_bookings", error.Code);
        Assert.Equal("PropertyModeBlockedByBookings", error.MessageKey);
        Assert.Equal(Day(21), Assert.Single(error.MessageArgs));
    }

    [Fact]
    public void EnsureAllowed_BlockedByLease_Is409WithTheFirstFreeDay()
    {
        var error = Assert.Throws<DomainConflictException>(
            () => PropertyModeRules.EnsureAllowed(Assess(RentalMode.Long, RentalMode.Short, Day(15), [Lease(Day(-5), Day(40))])));

        Assert.Equal("property_mode_blocked_by_lease", error.Code);
        Assert.Equal("PropertyModeBlockedByLease", error.MessageKey);
        Assert.Equal(Day(41), Assert.Single(error.MessageArgs));
    }

    [Fact]
    public void EnsureAllowed_BlockedByDraftLease_Is409WithoutADay()
    {
        var error = Assert.Throws<DomainConflictException>(
            () => PropertyModeRules.EnsureAllowed(Assess(RentalMode.Long, RentalMode.Short, Day(15), [Draft(Day(1), Day(2))])));

        Assert.Equal("property_mode_blocked_by_draft_lease", error.Code);
        Assert.Equal("PropertyModeBlockedByDraftLease", error.MessageKey);
        Assert.Empty(error.MessageArgs);
    }

    [Fact]
    public void EnsureAllowed_DateTooEarly_Is422WithTomorrow()
    {
        var error = Assert.Throws<DomainRuleException>(() => PropertyModeRules.EnsureAllowed(AssessToLong(Day(10), [])));

        Assert.Equal("property_mode_date_too_early", error.Code);
        Assert.Equal("PropertyModeDateTooEarly", error.MessageKey);
        Assert.Equal(Day(11), Assert.Single(error.MessageArgs));
    }

    [Fact]
    public void EnsureAllowed_DateTooFar_Is422WithTheLastDay()
    {
        var error = Assert.Throws<DomainRuleException>(() => PropertyModeRules.EnsureAllowed(AssessToLong(Day(10).AddYears(11), [])));

        Assert.Equal("property_mode_date_too_far", error.Code);
        Assert.Equal(PropertyModeRules.LastPossibleDay(Today), Assert.Single(error.MessageArgs));
    }

    [Fact]
    public void EnsureAllowed_ChangeExists_Is409AndComesBeforeTheOtherIssues()
    {
        var scheduled = new PropertyModeChange { Status = PropertyModeChangeStatus.Scheduled, EffectiveDate = Day(30) };
        var preview = PropertyModeRules.Assess(
            PropertyId, RentalMode.Short, RentalMode.Long, Today, Day(10), [Stay(Day(12), Day(25))], scheduled);

        var error = Assert.Throws<DomainConflictException>(() => PropertyModeRules.EnsureAllowed(preview));

        Assert.Equal("property_mode_change_exists", error.Code);
        Assert.Equal("PropertyModeChangeExists", error.MessageKey);
    }

    // ─── Failure reason of the job ──────────────────────────────────────────────────────────────────

    [Fact]
    public void FailureReasonOf_Nothing_IsNull()
    {
        Assert.Null(PropertyModeRules.FailureReasonOf([]));
    }

    [Fact]
    public void FailureReasonOf_StaysAndBlocksFirstThenLeasesThenDrafts()
    {
        Assert.Equal(
            PropertyModeErrorCodes.BlockedByBookings,
            PropertyModeRules.FailureReasonOf([Lease(Day(1), Day(30)), Block(Day(12), Day(15))]));
        Assert.Equal(
            PropertyModeErrorCodes.BlockedByBookings,
            PropertyModeRules.FailureReasonOf([Stay(Day(12), Day(15))]));
        Assert.Equal(
            PropertyModeErrorCodes.BlockedByLease,
            PropertyModeRules.FailureReasonOf([Draft(Day(1), Day(2)), Lease(Day(1), Day(30))]));
        Assert.Equal(
            PropertyModeErrorCodes.BlockedByDraftLease,
            PropertyModeRules.FailureReasonOf([Draft(Day(1), Day(2))]));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The given day of October 2026, midnight UTC (negative or past 31: the days around it).</summary>
    private static DateTime Day(int day) => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day - 1);

    private static PropertyModePreview AssessToLong(DateTime? requested, IEnumerable<PropertyModeBlocker> candidates) =>
        Assess(RentalMode.Short, RentalMode.Long, requested, candidates);

    private static PropertyModePreview Assess(
        RentalMode from, RentalMode to, DateTime? requested, IEnumerable<PropertyModeBlocker> candidates) =>
        PropertyModeRules.Assess(PropertyId, from, to, Today, requested, candidates, scheduled: null);

    private static PropertyModeBlocker Stay(DateTime checkIn, DateTime checkOut) => new(
        PropertyModeBlockerKind.Stay, Guid.NewGuid(), checkIn, checkOut, checkOut.AddDays(1), "Confirmed", "Direct");

    private static PropertyModeBlocker Block(DateTime start, DateTime end) => new(
        PropertyModeBlockerKind.ImportedBlock, Guid.NewGuid(), start, end, end.AddDays(1), Source: "Airbnb");

    private static PropertyModeBlocker Lease(DateTime start, DateTime end) => new(
        PropertyModeBlockerKind.Lease, Guid.NewGuid(), start, end, end.AddDays(1), "Registered");

    private static PropertyModeBlocker Draft(DateTime start, DateTime end) => new(
        PropertyModeBlockerKind.DraftLease, Guid.NewGuid(), start, end, FreeFrom: null, Status: "Draft");
}
