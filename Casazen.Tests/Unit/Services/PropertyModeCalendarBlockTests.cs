using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: the calendar block of a property that goes long-term (<see cref="CalendarBlockReason.ModeChange"/>). The portals
/// read it through the iCal export (a neutral all-day event from the night before the change, a stable UID), the single
/// occupancy rule takes its nights, and the host can neither create it nor remove it by hand: it goes away with the return to short
/// stays. Its other life (written at the programming, kept when applied, removed on cancel or failure) is in
/// <see cref="PropertyModeScheduleTests"/> and <see cref="PropertyModeApplyTests"/>.
/// </summary>
public class PropertyModeCalendarBlockTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ─── The iCal export ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_TheBlockOfAChangeToLong_IsAnAllDayEventFromTheDayForTwoYearsWithTheNeutralSummary()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        var block = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
        var exported = await _h.Db.CalendarBlocks.AsNoTracking().Where(ICalExportService.ExportsBlock).ToListAsync();

        var lines = new ICalExportService().BuildPropertyFeed([], exported, "Occupato").Split("\r\n");

        Assert.Contains($"UID:block-{block.Id}", lines);
        Assert.Contains("DTSTART;VALUE=DATE:20261019", lines);
        Assert.Contains("DTEND;VALUE=DATE:20281020", lines);
        Assert.Contains("SUMMARY:Occupato", lines);
        // Never the reason nor the word "mode".
        Assert.DoesNotContain(lines, l => l.Contains("ModeChange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Export_AfterTheChangeBackToShort_TheBlockIsGone()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedManualBlockAsync(property, Day(-30), Day(700), CalendarBlockReason.ModeChange);
        await _h.SeedChangeAsync(property, RentalMode.Short, Today);

        await _h.Service.ApplyDueAsync();

        var exported = await _h.Db.CalendarBlocks.AsNoTracking().Where(ICalExportService.ExportsBlock).ToListAsync();
        Assert.DoesNotContain("BEGIN:VEVENT", new ICalExportService().BuildPropertyFeed([], exported, "Occupato"));
    }

    [Fact]
    public void ExportsBlock_IsTheManualBlocksWithoutAFeed_SoTheBlockOfTheChangeIsOneOfThem()
    {
        var modeChange = new CalendarBlock
        {
            Source = CalendarBlockSource.Manual,
            FeedId = null,
            ManualReason = CalendarBlockReason.ModeChange,
        };

        Assert.True(ICalExportService.ExportsBlock.Compile()(modeChange));
    }

    [Fact]
    public async Task Occupancy_TheBlockOfTheChange_TakesTheNightsOfTheSingleOccupancyRule()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        _h.Db.ChangeTracker.Clear();

        // The booking checks and the public availability read the blocks through PropertyOccupancy: a stay on the closed
        // nights is refused, including the night that checks out on the day of the change. The night before that is free.
        Assert.True(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(19), Day(21))));
        Assert.False(await _h.Db.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(property.Id, Day(17), Day(19))));
    }

    // ─── The host cannot touch it ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_TheHostChoosesTheReasonModeChange_Is422InvalidReason()
    {
        var property = await _h.SeedPropertyAsync();
        var service = NewBlocks();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateAsync(
            new ManualBlockRequest(property.Id, Day(20), Day(25), CalendarBlockReason.ModeChange)));

        Assert.Equal(ManualBlockErrorCodes.InvalidReason, error.Code);
        Assert.False(await _h.Db.CalendarBlocks.AnyAsync());
    }

    [Theory]
    [InlineData(CalendarBlockReason.Owner)]
    [InlineData(CalendarBlockReason.Maintenance)]
    [InlineData(CalendarBlockReason.Other)]
    public async Task Create_TheThreeReasonsOfTheHost_StillWork(CalendarBlockReason reason)
    {
        var property = await _h.SeedPropertyAsync();

        var block = await NewBlocks().CreateAsync(new ManualBlockRequest(property.Id, Day(20), Day(25), reason));

        Assert.Equal(reason, block.ManualReason);
    }

    [Fact]
    public async Task Delete_TheBlockOfTheChange_IsRefusedAndKept()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        var block = Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => NewBlocks().DeleteAsync(block.Id));

        Assert.Equal("calendar_block_held_by_mode_change", error.Code);
        Assert.Equal(ManualBlockErrorCodes.HeldByModeChange, error.Code);
        Assert.Equal("CalendarBlockHeldByModeChange", error.MessageKey);
        Assert.Single(await _h.ModeChangeBlocksAsync(property.Id));
    }

    [Fact]
    public async Task Delete_TheOtherManualBlocksOfTheHost_StillWork()
    {
        var property = await _h.SeedPropertyAsync();
        var owner = await _h.SeedManualBlockAsync(property, Day(20), Day(25), CalendarBlockReason.Owner);

        await NewBlocks().DeleteAsync(owner.Id);

        Assert.False(await _h.Db.CalendarBlocks.AnyAsync(b => b.Id == owner.Id));
    }

    [Fact]
    public async Task List_TheBlockOfTheChange_IsInTheListOfTheManualBlocksWithItsReason()
    {
        var property = await _h.SeedPropertyAsync();
        await _h.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var blocks = await NewBlocks().ListManualAsync(property.Id);

        Assert.Equal(CalendarBlockReason.ModeChange, Assert.Single(blocks).ManualReason);
    }

    // ─── The channel manager is told (E08/F05/US02, NF-78) ─────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data rule. The block a property that goes long-term gets, and its removal, are told to the channel manager in the very save of the change (the process of the
    /// calendar change exists together with the mode change, never without it) and run once after the commit: a property on the channel manager does not stay sellable on the portals.
    /// Programming, cancelling, applying to short stays and a change that fails on the day are the four ways the block is written or removed.
    /// </summary>
    [Fact]
    public async Task TheChannelManagerIsToldInTheSameSave_WhenTheCalendarClosesAndWhenItOpensAgain_Test()
    {
        var property = await _h.SeedPropertyAsync();
        var told = new ToldChannelManager(_h, property.Id);

        // Programming a change to long-term writes the block from the night before its day for two years, and the change that closes those nights on the channel manager.
        var scheduled = await told.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var block = Assert.Single(await told.Db().CalendarBlocks.AsNoTracking().ToListAsync());
        var process = await told.Db().CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((CalendarChangeKind.Block, CalendarChangeState.Queued, 1), (process.Kind, process.State, process.PropertyCount));
        Assert.Contains("\"action\":\"created\"", process.SummaryJson);
        Assert.DoesNotContain("note", process.SummaryJson);
        var chunks = await told.Db().CalendarChangeChunks.AsNoTracking().Where(c => c.ProcessId == process.Id).OrderBy(c => c.FromDate).ToListAsync();
        Assert.All(chunks, c => Assert.Equal(CalendarChunkState.Pending, c.State));
        Assert.Equal(DateOnly.FromDateTime(block.StartUtc), chunks[0].FromDate);
        Assert.Equal(DateOnly.FromDateTime(block.EndUtc).AddDays(-1), chunks[^1].ToDate);
        told.VerifyRuns(Times.Once());

        // Cancelling it opens the nights again: a change of its own, in the save of the cancellation.
        await told.Service.CancelAsync(property.Id, scheduled.Id, UserId);

        Assert.Empty(await told.Db().CalendarBlocks.AsNoTracking().ToListAsync());
        var removal = Assert.Single(await told.Db().CalendarChangeProcesses.AsNoTracking().Where(p => p.SummaryJson.Contains("\"action\":\"removed\"")).ToListAsync());
        Assert.Equal((CalendarChangeKind.Block, CalendarChangeState.Queued), (removal.Kind, removal.State));
        told.VerifyRuns(Times.Exactly(2));
    }

    [Fact]
    public async Task TheChannelManagerIsToldWhenTheChangeIsApplied_ToLongTermAndBackToShortStays()
    {
        // Back to short stays: the block goes and the nights open again, on the channel manager too.
        var back = await _h.SeedPropertyAsync(RentalMode.Long);
        var toldBack = new ToldChannelManager(_h, back.Id);
        await _h.SeedManualBlockAsync(back, Day(-30), Day(700), CalendarBlockReason.ModeChange);
        await _h.SeedChangeAsync(back, RentalMode.Short, Today);

        await toldBack.Service.ApplyDueAsync();

        var reopened = Assert.Single(await toldBack.Db().CalendarChangeProcesses.AsNoTracking().ToListAsync());
        Assert.Equal((CalendarChangeKind.Block, CalendarChangeState.Queued), (reopened.Kind, reopened.State));
        Assert.Contains("\"action\":\"removed\"", reopened.SummaryJson);
        toldBack.VerifyRuns(Times.Once());

        // To long-term with no block yet (the change was written by hand): the block is written and told.
        var forth = await _h.SeedPropertyAsync();
        var toldForth = new ToldChannelManager(_h, forth.Id);
        await _h.SeedChangeAsync(forth, RentalMode.Long, Today);

        await toldForth.Service.ApplyDueAsync();

        Assert.Single(await toldForth.Db().CalendarBlocks.AsNoTracking().Where(b => b.PropertyId == forth.Id).ToListAsync());
        var closed = Assert.Single(await toldForth.Db().CalendarChangeProcesses.AsNoTracking().Where(p => p.SummaryJson.Contains(forth.Id.ToString())).ToListAsync());
        Assert.Contains("\"action\":\"created\"", closed.SummaryJson);
        toldForth.VerifyRuns(Times.Once());
    }

    /// <summary>
    /// Where the property books (a lookup that may call the channel manager) is asked once for each operation, before the transaction and the dates lock of the change of mode and
    /// before anything is staged: nothing of the unit of work is changed yet when the channel manager is asked, and the recorder does not ask again.
    /// </summary>
    [Fact]
    public async Task TheChannelManagerIsAskedOnce_BeforeTheTransactionAndBeforeAnythingIsStaged()
    {
        var property = await _h.SeedPropertyAsync();
        var told = new ToldChannelManager(_h, property.Id);
        var changedWhenAsked = new List<int>();
        told.Targets
            .Setup(t => t.LookupAsync(property.Id, It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((_, _) => changedWhenAsked.Add(_h.Db.ChangeTracker.Entries().Count(e => e.State != EntityState.Unchanged)))
            .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")));

        var scheduled = await told.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);
        await told.Service.CancelAsync(property.Id, scheduled.Id, UserId);

        Assert.Equal([0, 0], changedWhenAsked);
    }

    [Fact]
    public async Task APropertyThatIsNotOnTheChannelManager_RecordsAnAppliedChange_AndNothingIsRun()
    {
        var property = await _h.SeedPropertyAsync();
        var told = new ToldChannelManager(_h, property.Id, mirrored: false);

        await told.Service.ScheduleAsync(property.Id, RentalMode.Long, Day(20), UserId);

        var process = await told.Db().CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal(CalendarChangeState.Applied, process.State);
        Assert.Equal(0, await told.Db().CalendarChangeChunks.CountAsync());
        told.VerifyRuns(Times.Never());
    }

    private CalendarBlockService NewBlocks() =>
        new(_h.Db, Mock.Of<ICheckoutHoldExpiryService>(), NullLogger<CalendarBlockService>.Instance, _h.Clock);

    /// <summary>The mode change service with the real recorder of the calendar changes: the channel manager answers that the property books there (or not), a job client is watched.</summary>
    private sealed class ToldChannelManager
    {
        private readonly PropertyModeHarness _harness;

        public ToldChannelManager(PropertyModeHarness harness, Guid propertyId, bool mirrored = true)
        {
            _harness = harness;
            Targets
                .Setup(t => t.LookupAsync(propertyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mirrored ? OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")) : OctorateTargetLookup.NotMirrored);
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
            var recorder = new CalendarChangeRecorder(
                harness.Db,
                Targets.Object,
                flags.Object,
                Microsoft.Extensions.Options.Options.Create(new OctorateCalendarOptions()),
                harness.Clock,
                NullLogger<CalendarChangeRecorder>.Instance);
            Service = harness.NewService(harness.Db, null, recorder, Jobs.Object);
        }

        public PropertyModeService Service { get; }

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IBackgroundJobClient> Jobs { get; } = new();

        /// <summary>A context of its own on the same database: what is saved, whatever the service still tracks.</summary>
        public AppDbContext Db() => _harness.SecondContext();

        public void VerifyRuns(Times times) =>
            Jobs.Verify(
                j => j.Create(
                    It.Is<Job>(job => job.Type == typeof(CalendarChangeProcessJob) && job.Method.Name == nameof(CalendarChangeProcessJob.RunAsync)),
                    It.IsAny<IState>()),
                times);
    }
}
