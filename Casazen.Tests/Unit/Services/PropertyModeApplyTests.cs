using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: the hourly application of the scheduled changes (<c>property-mode-change</c> job). A change is applied at the
/// first run after midnight of Rome of its day, checked again against the stays, the imported blocks and the leases (a
/// change that no longer fits fails, with the reason and the host told, and never overlaps them), idempotent (two runs are
/// one application), and the property is evaluated again for the short-stay compliance when it comes back to short stays.
/// "Today" is 10 October 2026; Rome is on summer time until 25 October (midnight is 22:00 UTC).
/// </summary>
public class PropertyModeApplyTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ─── Applying ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_ChangeToLongOnItsDay_PutsThePropertyInLongModeAndClosesTheCalendar()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(new PropertyModeRunResult(Skipped: false, Examined: 1, Applied: 1, Failed: 0), result);
        var stored = await _h.ReloadPropertyAsync(property.Id);
        Assert.Equal(RentalMode.Long, stored.RentalMode);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAt);
        var applied = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(PropertyModeChangeStatus.Applied, applied.Status);
        Assert.Equal(Now.UtcDateTime, applied.AppliedAt);
        Assert.Null(applied.FailureReason);
        var block = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        Assert.Equal((Today.AddDays(-1), new DateTime(2028, 10, 10, 0, 0, 0, DateTimeKind.Utc)), (block.StartUtc, block.EndUtc));
        Assert.Equal(CalendarBlockSource.Manual, block.Source);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x => x.ChangeId == change.Id && x.Kind == PropertyModeNoticeKind.Applied && x.EarliestDate == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // Back to short stays only: the compliance of the booking site is not looked at for a property that goes long-term.
        _h.Compliance.Verify(c => c.ReevaluateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyDue_ChangeToShort_PutsThePropertyBackReopensTheCalendarAndEvaluatesItsCompliance()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedManualBlockAsync(property, Day(-30), Day(700), CalendarBlockReason.ModeChange);
        var owner = await _h.SeedManualBlockAsync(property, Day(40), Day(44), CalendarBlockReason.Owner);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Today);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Applied);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(change.Id)).Status);
        Assert.Empty(await _h.ModeChangeBlocksAsync(property.Id));
        // The host's own blocks stay.
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(b => b.Id == owner.Id));
        _h.Compliance.Verify(c => c.ReevaluateAsync(property.Id, It.IsAny<CancellationToken>()), Times.Once);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x => x.ChangeId == change.Id && x.Kind == PropertyModeNoticeKind.Applied),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyDue_TheComplianceIsEvaluatedAfterTheModeIsSaved()
    {
        // ReevaluateAsync ignores a long-term property (PM-01): it must see the property already back in short mode.
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedChangeAsync(property, RentalMode.Short, Today);
        RentalMode? modeSeenByTheEvaluation = null;
        _h.Compliance
            .Setup(c => c.ReevaluateAsync(property.Id, It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken _) =>
            {
                using var other = _h.SecondContext();
                modeSeenByTheEvaluation = (await other.Properties.AsNoTracking().SingleAsync(p => p.Id == id)).RentalMode;
                return new PropertyComplianceCheck(id, PropertyComplianceStatus.Active, PropertyComplianceStatus.Active, [], false);
            });

        await _h.Service.ApplyDueAsync();

        Assert.Equal(RentalMode.Short, modeSeenByTheEvaluation);
    }

    [Fact]
    public async Task ApplyDue_AChangeProgrammedByTheService_KeepsItsCalendarBlockWhenItIsApplied()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(11), UserId);
        var heldBefore = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));

        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 22, 0, 0, TimeSpan.Zero));
        await _h.Service.ApplyDueAsync();

        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(change.Id)).Status);
        var heldAfter = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        // The same block, the same event for the portals that already read it.
        Assert.Equal(heldBefore.Id, heldAfter.Id);
        Assert.Equal((Day(10), new DateTime(2028, 10, 11, 0, 0, 0, DateTimeKind.Utc)), (heldAfter.StartUtc, heldAfter.EndUtc));
    }

    [Fact]
    public async Task ApplyDue_AStaleBlockOfAnEarlierRound_IsReplacedByTheOneOfTheChange()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedManualBlockAsync(property, Day(-400), Day(300), CalendarBlockReason.ModeChange);
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        await _h.Service.ApplyDueAsync();

        var block = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        Assert.Equal(Today.AddDays(-1), block.StartUtc);
    }

    // ─── When ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_ChangeOfALaterDay_IsLeftAlone()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(11));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(new PropertyModeRunResult(false, 0, 0, 0), result);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Scheduled, (await _h.ReloadChangeAsync(change.Id)).Status);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyDue_SummerTime_IsAppliedAtTheFirstRunAfterMidnightOfRome()
    {
        // Midnight of Rome on 11 October is 22:00 UTC of the 10th (CEST, UTC+2).
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Day(11));

        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 21, 59, 59, TimeSpan.Zero));
        Assert.Equal(0, (await _h.Service.ApplyDueAsync()).Applied);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);

        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 22, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, (await _h.Service.ApplyDueAsync()).Applied);
        Assert.Equal(RentalMode.Long, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    [Fact]
    public async Task ApplyDue_WinterTime_IsAppliedAtTheFirstRunAfterMidnightOfRome()
    {
        // Midnight of Rome on 2 November is 23:00 UTC of the 1st (CET, UTC+1).
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 22, 59, 59, TimeSpan.Zero));
        Assert.Equal(0, (await _h.Service.ApplyDueAsync()).Applied);

        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, (await _h.Service.ApplyDueAsync()).Applied);
        Assert.Equal(RentalMode.Long, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    [Fact]
    public async Task ApplyDue_ARunAfterALongStop_AppliesWhatItMissed()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Day(3));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Applied);
        // The block is the one of the change (the night before its day), not of the day of the run.
        Assert.Equal(Day(2), Assert.Single(await _h.ModeChangeBlocksAsync(property.Id)).StartUtc);
    }

    [Fact]
    public async Task ApplyDue_SeveralChanges_AreAppliedByDayAndEachOnItsProperty()
    {
        var first = await _h.SeedPropertyAsync(name: "Prima");
        var second = await _h.SeedPropertyAsync(RentalMode.Long, first.OrgId, "Seconda");
        var third = await _h.SeedPropertyAsync(name: "Terza");
        await _h.SeedChangeAsync(first, RentalMode.Long, Day(10));
        await _h.SeedChangeAsync(second, RentalMode.Short, Day(9));
        var later = await _h.SeedChangeAsync(third, RentalMode.Long, Day(12));
        var order = new List<Guid>();
        _h.Notifications
            .Setup(n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()))
            .Callback((PropertyModeNotice notice, CancellationToken _) => order.Add(notice.ChangeId))
            .ReturnsAsync(true);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(new PropertyModeRunResult(false, 2, 2, 0), result);
        Assert.Equal(RentalMode.Long, (await _h.ReloadPropertyAsync(first.Id)).RentalMode);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(second.Id)).RentalMode);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(third.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Scheduled, (await _h.ReloadChangeAsync(later.Id)).Status);
        // The oldest day first.
        Assert.Equal(2, order.Count);
        Assert.Equal(second.Id, (await _h.ReloadChangeAsync(order[0])).PropertyId);
    }

    [Theory]
    [InlineData(PropertyModeChangeStatus.Applied)]
    [InlineData(PropertyModeChangeStatus.Cancelled)]
    [InlineData(PropertyModeChangeStatus.Failed)]
    public async Task ApplyDue_ChangesThatAreOver_AreNotExamined(PropertyModeChangeStatus status)
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Day(5), status);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(0, result.Examined);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    // ─── Idempotence ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_TwoRuns_AreOneApplication()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today);

        var first = await _h.Service.ApplyDueAsync();
        _h.Clock.Advance(TimeSpan.FromHours(1));
        var second = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, first.Applied);
        Assert.Equal(new PropertyModeRunResult(false, 0, 0, 0), second);
        Assert.Equal(Now.UtcDateTime, (await _h.ReloadChangeAsync(change.Id)).AppliedAt);
        Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyDue_ASecondRunOnAnotherContext_FindsTheChangeDone()
    {
        // Two runs that overlap (a retry, a manual trigger): each reads the row again before writing and finds it moved.
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Today);
        using var other = _h.SecondContext();
        var otherRun = _h.NewService(other);

        var first = await _h.Service.ApplyDueAsync();
        var second = await otherRun.ApplyDueAsync();

        Assert.Equal(1, first.Applied);
        Assert.Equal(0, second.Applied);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(change.Id)).Status);
        _h.Compliance.Verify(c => c.ReevaluateAsync(property.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyDue_ACancelledChange_IsNotApplied()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(11), UserId);
        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 22, 30, 0, TimeSpan.Zero));
        await _h.Service.CancelAsync(property.Id, change.Id, UserId);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(0, result.Applied);
        Assert.Equal(PropertyModeChangeStatus.Cancelled, (await _h.ReloadChangeAsync(change.Id)).Status);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    // ─── The change is checked again on its day ─────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_AStayArrivedAfterTheProgramming_FailsTheChangeToLongAndTellsTheHost()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(15), UserId);
        // A reservation of a portal read after the change was programmed (the OTA stays are not refused by the block).
        await _h.SeedStayAsync(property, Day(14), Day(20), source: BookingSource.Airbnb);
        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(new PropertyModeRunResult(false, 1, 0, 1), result);
        var stored = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(PropertyModeChangeStatus.Failed, stored.Status);
        Assert.Equal(PropertyModeErrorCodes.BlockedByBookings, stored.FailureReason);
        Assert.Equal(new DateTime(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc), stored.FailedAt);
        Assert.Null(stored.AppliedAt);
        // The property keeps its mode, and the dates the change had closed are open again.
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
        Assert.Empty(await _h.ModeChangeBlocksAsync(property.Id));
        // The host is told, with the first day that is free now.
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x =>
                    x.ChangeId == change.Id && x.Kind == PropertyModeNoticeKind.Failed && x.EarliestDate == Day(21)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _h.Compliance.Verify(c => c.ReevaluateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyDue_ABlockImportedAfterTheProgramming_FailsTheChange()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today);
        await _h.SeedImportedBlockAsync(property, Day(9), Day(13));

        await _h.Service.ApplyDueAsync();

        Assert.Equal(PropertyModeErrorCodes.BlockedByBookings, (await _h.ReloadChangeAsync(change.Id)).FailureReason);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    [Fact]
    public async Task ApplyDue_AStayThatLeavesTheDayBefore_IsNoObstacle()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);
        await _h.SeedStayAsync(property, Day(5), Day(9));
        await _h.SeedStayAsync(property, Day(8), Day(30), BookingStatus.Cancelled);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Applied);
    }

    [Fact]
    public async Task ApplyDue_AStayThatLeavesOnTheDayOfTheChange_StandsInTheWayLikeAtTheProgramming()
    {
        // "Last departure + 1": the same rule at the programming and on the day.
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Today);
        await _h.SeedStayAsync(property, Day(7), Day(10));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ApplyDue_ALateRun_ChecksTodayAndNotTheDayOfTheChange()
    {
        // The change was for the 5th; the run is on the 10th. A stay that left on the 7th is over; one still in the house is not.
        var free = await _h.SeedPropertyAsync(name: "Libera");
        var busy = await _h.SeedPropertyAsync(orgId: free.OrgId, name: "Occupata");
        var freeChange = await _h.SeedChangeAsync(free, RentalMode.Long, Day(5));
        var busyChange = await _h.SeedChangeAsync(busy, RentalMode.Long, Day(5));
        await _h.SeedStayAsync(free, Day(3), Day(7), BookingStatus.CheckedOut);
        await _h.SeedStayAsync(busy, Day(8), Day(12), BookingStatus.CheckedIn);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(new PropertyModeRunResult(false, 2, 1, 1), result);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(freeChange.Id)).Status);
        var failed = await _h.ReloadChangeAsync(busyChange.Id);
        Assert.Equal(PropertyModeChangeStatus.Failed, failed.Status);
        Assert.Equal(PropertyModeErrorCodes.BlockedByBookings, failed.FailureReason);
    }

    [Fact]
    public async Task ApplyDue_ALeaseThatEndsAfterTheDay_FailsTheChangeToShortAndTheBlockOfTheLongPropertyStays()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var held = await _h.SeedManualBlockAsync(property, Day(-30), Day(700), CalendarBlockReason.ModeChange);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Today);
        await _h.SeedLeaseAsync(property, Day(-3), Day(60), LeaseStatus.Signed);

        await _h.Service.ApplyDueAsync();

        var stored = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(PropertyModeChangeStatus.Failed, stored.Status);
        Assert.Equal(PropertyModeErrorCodes.BlockedByLease, stored.FailureReason);
        Assert.Equal(RentalMode.Long, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
        Assert.Equal(held.Id, Assert.Single(await _h.ModeChangeBlocksAsync(property.Id)).Id);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x => x.Kind == PropertyModeNoticeKind.Failed && x.EarliestDate == Day(61)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _h.Compliance.Verify(c => c.ReevaluateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyDue_ADraftLeaseCreatedAfterTheProgramming_FailsTheChangeToShort()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Today);
        await _h.SeedLeaseAsync(property, Day(100), Day(465), LeaseStatus.Draft);

        await _h.Service.ApplyDueAsync();

        Assert.Equal(PropertyModeErrorCodes.BlockedByDraftLease, (await _h.ReloadChangeAsync(change.Id)).FailureReason);
    }

    [Fact]
    public async Task ApplyDue_TheLeaseEndedOnTheDayBefore_Applies()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedChangeAsync(property, RentalMode.Short, Today);
        await _h.SeedLeaseAsync(property, Day(-300), Day(9));
        await _h.SeedLeaseAsync(property, Day(-300), Day(100), LeaseStatus.Rejected);

        Assert.Equal(1, (await _h.Service.ApplyDueAsync()).Applied);
    }

    [Fact]
    public async Task ApplyDue_ADeletedProperty_FailsTheChangeWithoutTellingAnybody()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(11), UserId);
        _h.Notifications.Invocations.Clear();
        _h.Db.ChangeTracker.Clear();
        var row = await _h.Db.Properties.SingleAsync(p => p.Id == property.Id);
        row.IsDeleted = true;
        await _h.Db.SaveChangesAsync();
        _h.Db.ChangeTracker.Clear();
        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 22, 30, 0, TimeSpan.Zero));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Failed);
        var stored = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(PropertyModeChangeStatus.Failed, stored.Status);
        Assert.Equal(PropertyModeErrorCodes.PropertyNotFound, stored.FailureReason);
        Assert.Empty(await _h.ModeChangeBlocksAsync(property.Id));
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyDue_ThePropertyWasPutBackByHand_FailsTheChangeAndLeavesTheBlockAlone()
    {
        // Programmed to long-term, but somebody set the mode with SQL since (the runbook way to correct a classification).
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var held = await _h.SeedManualBlockAsync(property, Day(-30), Day(700), CalendarBlockReason.ModeChange);
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today, from: RentalMode.Short);

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Failed);
        var stored = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(PropertyModeChangeStatus.Failed, stored.Status);
        Assert.Equal(PropertyModeErrorCodes.PropertyChanged, stored.FailureReason);
        Assert.Equal(held.Id, Assert.Single(await _h.ModeChangeBlocksAsync(property.Id)).Id);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x => x.Kind == PropertyModeNoticeKind.Failed && x.EarliestDate == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── What never undoes a change ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_TheNoticeFails_TheChangeIsApplied()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Today);
        _h.Notifications
            .Setup(n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Applied);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(change.Id)).Status);
    }

    [Fact]
    public async Task ApplyDue_TheComplianceEvaluationFails_TheChangeIsAppliedAndTheNoticeStillGoes()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var change = await _h.SeedChangeAsync(property, RentalMode.Short, Today);
        _h.Compliance
            .Setup(c => c.ReevaluateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database busy"));

        var result = await _h.Service.ApplyDueAsync();

        Assert.Equal(1, result.Applied);
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await _h.ReloadChangeAsync(change.Id)).Status);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x => x.Kind == PropertyModeNoticeKind.Applied), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyDue_ACancelledToken_StopsTheRunBeforeAnyChange()
    {
        var first = await _h.SeedPropertyAsync(name: "Prima");
        var second = await _h.SeedPropertyAsync(orgId: first.OrgId, name: "Seconda");
        await _h.SeedChangeAsync(first, RentalMode.Long, Today);
        await _h.SeedChangeAsync(second, RentalMode.Long, Today);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Service.ApplyDueAsync(cts.Token));

        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(first.Id)).RentalMode);
    }
}
