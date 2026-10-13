using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: programming and withdrawing a change of rental mode. One change waiting per property; the same checks as the
/// preview, made again at the creation; to long-term the calendar closes from the night before the day, at once, for two
/// years (the booking site, the host and the portals that read the export stop taking those nights); the host is told
/// after the commit.
/// "Today" is 10 October 2026.
/// </summary>
public class PropertyModeScheduleTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ─── Schedule ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_ToLongNothingInTheWay_WritesAScheduledChangeAndTellsTheHost()
    {
        var property = await _h.SeedPropertyAsync();

        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var stored = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(property.Id, stored.PropertyId);
        Assert.Equal(property.OrgId, stored.OrgId);
        Assert.Equal((RentalMode.Short, RentalMode.Long), (stored.FromMode, stored.ToMode));
        Assert.Equal(Day(20), stored.EffectiveDate);
        Assert.Equal(PropertyModeChangeStatus.Scheduled, stored.Status);
        Assert.Equal(UserId, stored.CreatedByUserId);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAt);
        Assert.Null(stored.AppliedAt);
        Assert.Null(stored.FailureReason);
        // Nothing changes on the property until its day.
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(
                It.Is<PropertyModeNotice>(x => x.ChangeId == change.Id && x.Kind == PropertyModeNoticeKind.Scheduled),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Schedule_ToLong_ClosesTheCalendarFromTheNightBeforeTheDayForTwoYears()
    {
        var property = await _h.SeedPropertyAsync();

        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var block = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        Assert.Equal(CalendarBlockSource.Manual, block.Source);
        Assert.Null(block.FeedId);
        Assert.Equal(property.OrgId, block.OrgId);
        // The night of the 19th checks out on the morning of the 20th, which the change treats as not free yet.
        Assert.Equal(Day(19), block.StartUtc);
        Assert.Equal(new DateTime(2028, 10, 20, 0, 0, 0, DateTimeKind.Utc), block.EndUtc);
        Assert.Null(block.Summary);
    }

    [Fact]
    public async Task Schedule_ToLong_AnEarlierStayStaysFree_TheCheckoutNightOfTheChangeIsClosed()
    {
        // The stay of the 12th to the 15th leaves on the morning of the 15th: free from the 16th, so it does not stand
        // in the way. The block starts the night before the change and does not cover that stay.
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(15));

        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(16), UserId);

        var block = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        Assert.Equal(Day(15), block.StartUtc);
        _h.Db.ChangeTracker.Clear();
        Assert.False(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(12), Day(15))));
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(15), Day(16))));
    }

    [Fact]
    public async Task Schedule_ToShort_WritesTheChangeAndClosesNothing()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedLeaseAsync(property, Day(-100), Day(30));

        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(31), UserId);

        Assert.Equal((RentalMode.Long, RentalMode.Short), ((await _h.ReloadChangeAsync(change.Id)).FromMode, change.ToMode));
        Assert.Empty(await _h.ModeChangeBlocksAsync(property.Id));
        Assert.Equal(RentalMode.Long, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    [Fact]
    public async Task Schedule_ToShort_LeavesTheBlockOfTheLongTermPropertyWhereItIs()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var held = await _h.SeedManualBlockAsync(property, Day(-5), Day(700), CalendarBlockReason.ModeChange);

        await _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(20), UserId);

        Assert.Equal(held.Id, Assert.Single(await _h.ModeChangeBlocksAsync(property.Id)).Id);
    }

    [Fact]
    public async Task Schedule_TheTimeOfTheDayIsIgnored_TheChangeIsOnTheCalendarDay()
    {
        var property = await _h.SeedPropertyAsync();

        var change = await _h.Service.ScheduleAsync(
            property.Id, RentalMode.Long, new DateTime(2026, 10, 20, 17, 45, 0, DateTimeKind.Unspecified), UserId);

        Assert.Equal(Day(20), change.EffectiveDate);
        Assert.Equal(DateTimeKind.Utc, change.EffectiveDate.Kind);
    }

    [Fact]
    public async Task Schedule_AChangeAlreadyWaits_Is409AndWritesNothing()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(40), UserId));

        Assert.Equal(PropertyModeErrorCodes.ChangeExists, error.Code);
        Assert.Equal("PropertyModeChangeExists", error.MessageKey);
        Assert.Equal(1, await _h.Db.PropertyModeChanges.CountAsync());
        Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Schedule_AfterTheWaitingOneIsCancelled_AnotherCanBeProgrammed()
    {
        var property = await _h.SeedPropertyAsync();
        var first = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        await _h.Service.CancelAsync(property.Id, first.Id, UserId);

        var second = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(25), UserId);

        Assert.NotEqual(first.Id, second.Id);
        var state = await _h.Service.GetStateAsync(property.Id);
        Assert.Equal(second.Id, state.Scheduled!.Id);
        Assert.Equal(first.Id, state.Last!.Id);
        Assert.Equal(Day(24), Assert.Single(await _h.ModeChangeBlocksAsync(property.Id)).StartUtc);
    }

    [Fact]
    public async Task Schedule_TwoPropertiesOfTheSameOrg_EachHasItsOwnChange()
    {
        var first = await _h.SeedPropertyAsync();
        var second = await _h.SeedPropertyAsync(orgId: first.OrgId, name: "Altra casa");

        await _h.Service.ScheduleAsync(first.Id, RentalMode.Long, Day(20), UserId);
        await _h.Service.ScheduleAsync(second.Id, RentalMode.Long, Day(20), UserId);

        Assert.Equal(2, await _h.Db.PropertyModeChanges.CountAsync(c => c.Status == PropertyModeChangeStatus.Scheduled));
    }

    [Fact]
    public async Task Schedule_ToLongBeforeTheLastDeparture_Is409AndWritesNothing()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(25));

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId));

        Assert.Equal(PropertyModeErrorCodes.BlockedByBookings, error.Code);
        Assert.Equal(Day(26), Assert.Single(error.MessageArgs));
        Assert.Equal(0, await _h.Db.PropertyModeChanges.CountAsync());
        Assert.Empty(await _h.ModeChangeBlocksAsync(property.Id));
        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Schedule_ToLong_WorksOnTheDayAfterTheLastDepartureOrBlock()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(25));
        await _h.SeedImportedBlockAsync(property, Day(26), Day(28));

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(26), UserId));
        Assert.Equal(Day(29), Assert.Single(error.MessageArgs));

        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(29), UserId);
        Assert.Equal(Day(29), change.EffectiveDate);
    }

    [Fact]
    public async Task Schedule_ToShortWhileALeaseRuns_Is409()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedLeaseAsync(property, Day(-100), Day(50));

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(50), UserId));

        Assert.Equal(PropertyModeErrorCodes.BlockedByLease, error.Code);
        Assert.Equal(Day(51), Assert.Single(error.MessageArgs));
        Assert.Equal(0, await _h.Db.PropertyModeChanges.CountAsync());
        Assert.NotNull(await _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(51), UserId));
    }

    [Fact]
    public async Task Schedule_ToShortWithADraftLease_Is409UntilTheDraftIsDeleted()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var draft = await _h.SeedLeaseAsync(property, Day(100), Day(400), LeaseStatus.Draft);

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(20), UserId));
        Assert.Equal(PropertyModeErrorCodes.BlockedByDraftLease, error.Code);

        _h.Db.LeaseContracts.Remove(await _h.Db.LeaseContracts.SingleAsync(l => l.Id == draft.Id));
        await _h.Db.SaveChangesAsync();
        Assert.NotNull(await _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(20), UserId));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2)]
    public async Task Schedule_TodayOrThePast_Is422WithTomorrow(int day)
    {
        var property = await _h.SeedPropertyAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(day), UserId));

        Assert.Equal(PropertyModeErrorCodes.DateTooEarly, error.Code);
        Assert.Equal(Day(11), Assert.Single(error.MessageArgs));
        Assert.Equal(0, await _h.Db.PropertyModeChanges.CountAsync());
    }

    [Fact]
    public async Task Schedule_Tomorrow_IsTheFirstDayAllowed()
    {
        var property = await _h.SeedPropertyAsync();

        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(11), UserId);

        Assert.Equal(Day(11), change.EffectiveDate);
    }

    [Fact]
    public async Task Schedule_ADayMoreThanTenYearsAway_Is422DateTooFar()
    {
        var property = await _h.SeedPropertyAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, new DateTime(2206, 1, 1), UserId));

        Assert.Equal(PropertyModeErrorCodes.DateTooFar, error.Code);
    }

    [Fact]
    public async Task Schedule_AlreadyInThatMode_Is422()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId));

        Assert.Equal(PropertyModeErrorCodes.AlreadyInMode, error.Code);
    }

    [Fact]
    public async Task Schedule_UnknownProperty_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => _h.Service.ScheduleAsync(Guid.NewGuid(), RentalMode.Long, Day(20), UserId));
    }

    [Fact]
    public async Task Schedule_WithoutAUser_Throws()
    {
        var property = await _h.SeedPropertyAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), " "));
    }

    [Fact]
    public async Task Schedule_TheNoticeFails_TheChangeIsKept()
    {
        var property = await _h.SeedPropertyAsync();
        _h.Notifications
            .Setup(n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        Assert.Equal(PropertyModeChangeStatus.Scheduled, (await _h.ReloadChangeAsync(change.Id)).Status);
        Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
    }

    [Fact]
    public async Task Schedule_TheNoticeIsNotQueued_TheChangeIsKept()
    {
        var property = await _h.SeedPropertyAsync();
        _h.Notifications
            .Setup(n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        Assert.Equal(PropertyModeChangeStatus.Scheduled, (await _h.ReloadChangeAsync(change.Id)).Status);
    }

    // ─── Cancel ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_AWaitingChangeToLong_IsWithdrawnAndTheCalendarOpensAgain()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        _h.Clock.Advance(TimeSpan.FromHours(2));

        await _h.Service.CancelAsync(property.Id, change.Id, "auth0|admin");

        var stored = await _h.ReloadChangeAsync(change.Id);
        Assert.Equal(PropertyModeChangeStatus.Cancelled, stored.Status);
        Assert.Equal(Now.UtcDateTime.AddHours(2), stored.CancelledAt);
        Assert.Equal("auth0|admin", stored.CancelledByUserId);
        Assert.Null(stored.AppliedAt);
        Assert.Empty(await _h.ModeChangeBlocksAsync(property.Id));
        Assert.Equal(RentalMode.Short, (await _h.ReloadPropertyAsync(property.Id)).RentalMode);
    }

    [Fact]
    public async Task Cancel_AWaitingChangeToShort_LeavesTheBlockOfTheLongTermProperty()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var held = await _h.SeedManualBlockAsync(property, Day(-5), Day(700), CalendarBlockReason.ModeChange);
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Short, Day(20), UserId);

        await _h.Service.CancelAsync(property.Id, change.Id, UserId);

        Assert.Equal(PropertyModeChangeStatus.Cancelled, (await _h.ReloadChangeAsync(change.Id)).Status);
        Assert.Equal(held.Id, Assert.Single(await _h.ModeChangeBlocksAsync(property.Id)).Id);
    }

    [Fact]
    public async Task Cancel_TheManualBlocksOfTheHost_AreNeverTouched()
    {
        var property = await _h.SeedPropertyAsync();
        var owner = await _h.SeedManualBlockAsync(property, Day(40), Day(45), CalendarBlockReason.Owner);
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        await _h.Service.CancelAsync(property.Id, change.Id, UserId);

        _h.Db.ChangeTracker.Clear();
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(b => b.Id == owner.Id));
    }

    [Theory]
    [InlineData(PropertyModeChangeStatus.Applied)]
    [InlineData(PropertyModeChangeStatus.Cancelled)]
    [InlineData(PropertyModeChangeStatus.Failed)]
    public async Task Cancel_AChangeThatIsOver_Is409AndNothingChanges(PropertyModeChangeStatus status)
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.SeedChangeAsync(property, RentalMode.Long, Day(-1), status);

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.CancelAsync(property.Id, change.Id, UserId));

        Assert.Equal(PropertyModeErrorCodes.ChangeNotScheduled, error.Code);
        Assert.Equal(status, (await _h.ReloadChangeAsync(change.Id)).Status);
    }

    [Fact]
    public async Task Cancel_UnknownChangeOrAChangeOfAnotherProperty_IsNotFound()
    {
        var property = await _h.SeedPropertyAsync();
        var other = await _h.SeedPropertyAsync(orgId: property.OrgId, name: "Altra casa");
        var change = await _h.Service.ScheduleAsync(other.Id, RentalMode.Long, Day(20), UserId);

        var unknown = await Assert.ThrowsAsync<NotFoundException>(
            () => _h.Service.CancelAsync(property.Id, Guid.NewGuid(), UserId));
        var wrongProperty = await Assert.ThrowsAsync<NotFoundException>(
            () => _h.Service.CancelAsync(property.Id, change.Id, UserId));

        Assert.Equal(PropertyModeErrorCodes.ChangeNotFound, unknown.Code);
        Assert.Equal("PropertyModeChangeNotFound", wrongProperty.MessageKey);
        Assert.Equal(PropertyModeChangeStatus.Scheduled, (await _h.ReloadChangeAsync(change.Id)).Status);
    }

    [Fact]
    public async Task Cancel_TwiceInARow_TheSecondFindsItWithdrawn()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        await _h.Service.CancelAsync(property.Id, change.Id, UserId);

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _h.Service.CancelAsync(property.Id, change.Id, UserId));

        Assert.Equal(PropertyModeErrorCodes.ChangeNotScheduled, error.Code);
    }

    [Fact]
    public async Task Cancel_NoNoticeIsSent()
    {
        var property = await _h.SeedPropertyAsync();
        var change = await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        _h.Notifications.Invocations.Clear();

        await _h.Service.CancelAsync(property.Id, change.Id, UserId);

        _h.Notifications.Verify(
            n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── The booking side of the closed calendar ────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_ToLong_TheClosedNightsAreTakenByTheSingleOccupancyRule()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        // The rule the public availability, the booking checks and the export use (BK-05). The night before the day is
        // taken too: a stay on it checks out on the morning of the change. The night before that stays free.
        _h.Db.ChangeTracker.Clear();
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(19), Day(20))));
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(20), Day(21))));
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(400), Day(401))));
        Assert.False(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(18), Day(19))));
        Assert.False(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(900), Day(901))));
    }
}
