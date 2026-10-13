using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
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

    private CalendarBlockService NewBlocks() =>
        new(_h.Db, Mock.Of<ICheckoutHoldExpiryService>(), NullLogger<CalendarBlockService>.Instance, _h.Clock);
}
