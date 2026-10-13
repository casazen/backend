using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: the preview of a change of rental mode reads what stands in the way, in both directions. To long-term: the stays
/// (pending, confirmed, checked in) and the blocks imported from the portal calendars, last departure + 1. To short stays:
/// the leases that are not drafts nor rejected, <c>EndDate</c> + 1, and the drafts, which are deleted first. Never before
/// tomorrow. "Today" is 10 October 2026.
/// </summary>
public class PropertyModePreviewTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ─── Short stays to long-term ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToLong_NothingInTheWay_TheFirstDayIsTomorrowAndItCanBeScheduled()
    {
        var property = await _h.SeedPropertyAsync();

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long);

        Assert.Equal(Day(11), preview.EarliestDate);
        Assert.Equal(Day(11), preview.Date);
        Assert.Equal(Today, preview.Today);
        Assert.True(preview.CanSchedule);
        Assert.Empty(preview.Blockers);
        Assert.Equal((RentalMode.Short, RentalMode.Long), (preview.From, preview.To));
        Assert.Null(preview.ScheduledChange);
    }

    [Fact]
    public async Task ToLong_StaysToCome_TheFirstDayIsTheDayAfterTheLastDeparture()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(15));
        var last = await _h.SeedStayAsync(property, Day(20), Day(24), BookingStatus.Pending, BookingSource.Manual);
        await _h.SeedStayAsync(property, Day(8), Day(12), BookingStatus.CheckedIn);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(12));

        Assert.Equal(Day(25), preview.EarliestDate);
        Assert.False(preview.CanSchedule);
        Assert.Equal([PropertyModeErrorCodes.BlockedByBookings], preview.Issues);
        Assert.Equal(3, preview.Blockers.Count);
        var listed = preview.Blockers.Single(b => b.Id == last.Id);
        Assert.Equal(PropertyModeBlockerKind.Stay, listed.Kind);
        Assert.Equal(Day(20), listed.Start);
        Assert.Equal(Day(24), listed.End);
        Assert.Equal(Day(25), listed.FreeFrom);
        Assert.Equal("Pending", listed.Status);
        Assert.Equal("Manual", listed.Source);
        // Ordered by arrival: the stay in the house, then the two to come.
        Assert.Equal([Day(8), Day(12), Day(20)], preview.Blockers.Select(b => b.Start));
    }

    [Fact]
    public async Task ToLong_OnTheEarliestDay_NothingIsInTheWay()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(15));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(16));

        Assert.True(preview.CanSchedule);
        Assert.Empty(preview.Blockers);
    }

    [Fact]
    public async Task ToLong_StaysThatDoNotCount_AreNotInTheWay()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(property, Day(12), Day(30), BookingStatus.Cancelled);
        await _h.SeedStayAsync(property, Day(12), Day(30), BookingStatus.CheckedOut);
        // Already gone: left today (free from tomorrow).
        await _h.SeedStayAsync(property, Day(7), Day(10));
        // An abandoned checkout hold of the public site (30 minutes, BK-21) takes no dates.
        await _h.SeedStayAsync(property, Day(12), Day(30), BookingStatus.Pending, createdAt: Now.UtcDateTime.AddHours(-3), checkoutHold: true);
        // Another property's stay.
        var other = await _h.SeedPropertyAsync(orgId: property.OrgId, name: "Altra casa");
        await _h.SeedStayAsync(other, Day(12), Day(40));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long);

        Assert.Equal(Day(11), preview.EarliestDate);
        Assert.Empty(preview.Blockers);
    }

    [Fact]
    public async Task ToLong_ALiveCheckoutHold_IsAStayInTheWay()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedStayAsync(
            property, Day(12), Day(14), BookingStatus.Pending, createdAt: Now.UtcDateTime.AddMinutes(-5), checkoutHold: true);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long);

        Assert.Equal(Day(15), preview.EarliestDate);
    }

    [Fact]
    public async Task ToLong_ABlockImportedFromAPortal_IsInTheWayWithItsChannel()
    {
        var property = await _h.SeedPropertyAsync();
        var airbnb = await _h.SeedImportedBlockAsync(property, Day(14), Day(22), ICalFeedChannel.Airbnb);
        await _h.SeedImportedBlockAsync(property, Day(2), Day(8), ICalFeedChannel.BookingCom);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(15));

        var block = Assert.Single(preview.Blockers);
        Assert.Equal((PropertyModeBlockerKind.ImportedBlock, airbnb.Id), (block.Kind, block.Id));
        Assert.Equal("Airbnb", block.Source);
        Assert.Null(block.Status);
        Assert.Equal(Day(23), preview.EarliestDate);
    }

    [Fact]
    public async Task ToLong_AnImportedBlockWithoutFeed_IsStillInTheWay()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedImportedBlockAsync(property, Day(14), Day(22), channel: null);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(15));

        Assert.Equal("Other", Assert.Single(preview.Blockers).Source);
    }

    [Fact]
    public async Task ToLong_ABlockStandingForAnOtaStay_IsCountedOnceThroughTheStay()
    {
        var property = await _h.SeedPropertyAsync();
        var stay = await _h.SeedStayAsync(property, Day(14), Day(22), source: BookingSource.Airbnb);
        await _h.SeedImportedBlockAsync(property, Day(14), Day(22), bookingId: stay.Id);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(15));

        var only = Assert.Single(preview.Blockers);
        Assert.Equal((PropertyModeBlockerKind.Stay, stay.Id), (only.Kind, only.Id));
    }

    [Fact]
    public async Task ToLong_ABlockWhoseOtaStayWasCancelled_IsInTheWayAgainOnItsOwn()
    {
        var property = await _h.SeedPropertyAsync();
        var stay = await _h.SeedStayAsync(property, Day(14), Day(22), BookingStatus.Cancelled, BookingSource.Airbnb);
        var block = await _h.SeedImportedBlockAsync(property, Day(14), Day(22), bookingId: stay.Id);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(15));

        Assert.Equal(block.Id, Assert.Single(preview.Blockers).Id);
    }

    [Fact]
    public async Task ToLong_TheManualBlocksOfTheHost_AreNotInTheWay()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedManualBlockAsync(property, Day(14), Day(40), CalendarBlockReason.Owner);
        await _h.SeedManualBlockAsync(property, Day(50), Day(60), CalendarBlockReason.Maintenance);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long);

        Assert.Equal(Day(11), preview.EarliestDate);
        Assert.Empty(preview.Blockers);
    }

    [Fact]
    public async Task ToLong_LeasesOfTheProperty_AreNotInTheWayOfTheMode()
    {
        // No constraint on the leases of a short-stay property (PM-01): they do not stop it from going long-term.
        var property = await _h.SeedPropertyAsync();
        await _h.SeedLeaseAsync(property, Day(-30), Day(300));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long);

        Assert.True(preview.CanSchedule);
    }

    // ─── Long-term to short stays ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToShort_NoLease_TheFirstDayIsTomorrow()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Short);

        Assert.Equal(Day(11), preview.EarliestDate);
        Assert.True(preview.CanSchedule);
        Assert.Equal((RentalMode.Long, RentalMode.Short), (preview.From, preview.To));
    }

    [Fact]
    public async Task ToShort_ALeaseInForce_TheFirstDayIsTheDayAfterItsEnd()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var lease = await _h.SeedLeaseAsync(property, Day(-200), Day(60));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Short, Day(30));

        Assert.Equal(Day(61), preview.EarliestDate);
        Assert.Equal([PropertyModeErrorCodes.BlockedByLease], preview.Issues);
        var listed = Assert.Single(preview.Blockers);
        Assert.Equal((PropertyModeBlockerKind.Lease, lease.Id), (listed.Kind, listed.Id));
        Assert.Equal(Day(-200), listed.Start);
        Assert.Equal(Day(60), listed.End);
        Assert.Equal(Day(61), listed.FreeFrom);
        Assert.Equal("Registered", listed.Status);
        Assert.Null(listed.Source);
        Assert.True((await _h.Service.PreviewAsync(property.Id, RentalMode.Short, Day(61))).CanSchedule);
    }

    [Theory]
    [InlineData(LeaseStatus.AwaitingSignature)]
    [InlineData(LeaseStatus.PartiallySigned)]
    [InlineData(LeaseStatus.Signed)]
    [InlineData(LeaseStatus.RegistrationPending)]
    [InlineData(LeaseStatus.SentToProvider)]
    [InlineData(LeaseStatus.Registered)]
    public async Task ToShort_EveryLeaseThatIsNotADraftNorRejected_IsInTheWay(LeaseStatus status)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedLeaseAsync(property, Day(-10), Day(90), status);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Short);

        Assert.Equal(Day(91), preview.EarliestDate);
    }

    [Fact]
    public async Task ToShort_LeasesThatDoNotCount_AreNotInTheWay()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        // Rejected, and a lease that ended before today.
        await _h.SeedLeaseAsync(property, Day(-10), Day(90), LeaseStatus.Rejected);
        await _h.SeedLeaseAsync(property, Day(-400), Day(-5));
        // A lease that ends today is free from tomorrow.
        await _h.SeedLeaseAsync(property, Day(-400), Day(10));
        // Another property's lease.
        var other = await _h.SeedPropertyAsync(RentalMode.Long, property.OrgId, "Altra casa");
        await _h.SeedLeaseAsync(other, Day(-10), Day(500));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Short);

        Assert.Equal(Day(11), preview.EarliestDate);
        Assert.True(preview.CanSchedule);
        Assert.Empty(preview.Blockers);
    }

    [Fact]
    public async Task ToShort_ADraftLease_IsListedAndBlocksWhateverTheDate()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var draft = await _h.SeedLeaseAsync(property, Day(400), Day(765), LeaseStatus.Draft);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Short, Day(1000));

        Assert.Equal([PropertyModeErrorCodes.BlockedByDraftLease], preview.Issues);
        var listed = Assert.Single(preview.Blockers);
        Assert.Equal((PropertyModeBlockerKind.DraftLease, draft.Id), (listed.Kind, listed.Id));
        Assert.Null(listed.FreeFrom);
        Assert.Equal("Draft", listed.Status);
        // No date frees the property from a draft: the earliest day is still tomorrow.
        Assert.Equal(Day(11), preview.EarliestDate);
    }

    // ─── Common ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    public async Task Preview_TodayOrThePast_IsDateTooEarly(int day)
    {
        var property = await _h.SeedPropertyAsync();

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(day));

        Assert.Equal([PropertyModeErrorCodes.DateTooEarly], preview.Issues);
        Assert.False(preview.CanSchedule);
    }

    [Fact]
    public async Task Preview_AChangeAlreadyWaits_ItIsReturnedAndNoOtherCanBeScheduled()
    {
        var property = await _h.SeedPropertyAsync();
        var scheduled = await _h.SeedChangeAsync(property, RentalMode.Long, Day(30));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(40));

        Assert.Equal(scheduled.Id, preview.ScheduledChange!.Id);
        Assert.Equal([PropertyModeErrorCodes.ChangeExists], preview.Issues);
    }

    [Fact]
    public async Task Preview_ChangesThatAreOver_DoNotBlockANewOne()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.SeedChangeAsync(property, RentalMode.Long, Day(-30), PropertyModeChangeStatus.Failed);
        await _h.SeedChangeAsync(property, RentalMode.Long, Day(-20), PropertyModeChangeStatus.Cancelled);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long, Day(40));

        Assert.True(preview.CanSchedule);
        Assert.Null(preview.ScheduledChange);
    }

    [Fact]
    public async Task Preview_AlreadyInThatMode_Is422()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => _h.Service.PreviewAsync(property.Id, RentalMode.Long));

        Assert.Equal(PropertyModeErrorCodes.AlreadyInMode, error.Code);
        Assert.Equal("PropertyModeAlreadySet", error.MessageKey);
    }

    [Fact]
    public async Task Preview_ATargetThatIsNoMode_Is422()
    {
        var property = await _h.SeedPropertyAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => _h.Service.PreviewAsync(property.Id, (RentalMode)7));

        Assert.Equal(PropertyModeErrorCodes.TargetInvalid, error.Code);
    }

    [Fact]
    public async Task Preview_UnknownProperty_IsNotFoundWithItsCode()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => _h.Service.PreviewAsync(Guid.NewGuid(), RentalMode.Long));

        Assert.Equal(PropertyModeErrorCodes.PropertyNotFound, error.Code);
        Assert.Equal("PropertyNotFound", error.MessageKey);
    }

    [Fact]
    public async Task Preview_ASoftDeletedProperty_IsNotFound()
    {
        var property = await _h.SeedPropertyAsync();
        property.IsDeleted = true;
        _h.Db.Properties.Update(property);
        await _h.Db.SaveChangesAsync();
        _h.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<NotFoundException>(() => _h.Service.PreviewAsync(property.Id, RentalMode.Long));
    }

    [Fact]
    public async Task Preview_AfterTwentyTwoUtc_TodayIsAlreadyTheNextDayInRome()
    {
        var property = await _h.SeedPropertyAsync();
        // 23:30 UTC on the 10th is 01:30 of the 11th in Rome (CEST): tomorrow is the 12th.
        _h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 23, 30, 0, TimeSpan.Zero));

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Long);

        Assert.Equal(Day(11), preview.Today);
        Assert.Equal(Day(12), preview.EarliestDate);
    }

    [Fact]
    public async Task GetState_GivesTheModeTheWaitingChangeAndTheLastOneThatIsOver()
    {
        var property = await _h.SeedPropertyAsync();
        var older = await _h.SeedChangeAsync(property, RentalMode.Long, Day(-30), PropertyModeChangeStatus.Cancelled);
        var newer = await _h.SeedChangeAsync(property, RentalMode.Long, Day(-20), PropertyModeChangeStatus.Failed);
        var waiting = await _h.SeedChangeAsync(property, RentalMode.Long, Day(30));

        var state = await _h.Service.GetStateAsync(property.Id);

        Assert.Equal(RentalMode.Short, state.Mode);
        Assert.Equal(waiting.Id, state.Scheduled!.Id);
        Assert.NotNull(state.Last);
        Assert.Contains(state.Last!.Id, new[] { older.Id, newer.Id });
        Assert.NotEqual(waiting.Id, state.Last.Id);
    }

    [Fact]
    public async Task GetState_NothingYet_HasNoChanges()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);

        var state = await _h.Service.GetStateAsync(property.Id);

        Assert.Equal(RentalMode.Long, state.Mode);
        Assert.Null(state.Scheduled);
        Assert.Null(state.Last);
    }

    [Fact]
    public async Task GetState_UnknownProperty_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Service.GetStateAsync(Guid.NewGuid()));
    }
}
