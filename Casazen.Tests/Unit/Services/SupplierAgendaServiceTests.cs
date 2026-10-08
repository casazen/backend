using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="SupplierAgendaService"/> (SP-03, <c>api/supplier/availability/*</c> and <c>api/supplier/calendar</c>) on an
/// in-memory database: the weekly hours, time off, blocks and extra openings, the rules, the calendar and the input of the
/// planner; validation, limits, that a write leaves nothing behind when it is refused, and that a supplier never reaches the
/// agenda of another. What needs PostgreSQL (the lock, the unique index, the checks, parallel writes) is in
/// <c>SupplierAgendaPostgresTests</c>.
/// </summary>
public class SupplierAgendaServiceTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    // Thursday 8 October 2026, 12:00 in Rome (summer time).
    private static readonly DateTimeOffset Instant = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 8);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(Instant);

    // ─── Weekly hours ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHoursAsync_ASupplierThatNeverSavedAny_HasNoBandAndNoConfiguredMoment_AndNothingIsWritten()
    {
        var hours = await Service().GetHoursAsync(OrgA);

        Assert.Empty(hours.Bands);
        Assert.Null(hours.ConfiguredAt);
        await using var db = CreateDb();
        Assert.False(await db.SupplierSettings.Where(s => s.OrgId == OrgA).AnyAsync());
        Assert.False(await db.SupplierWorkingHours.Where(h => h.OrgId == OrgA).AnyAsync());
    }

    [Fact]
    public async Task ReplaceHoursAsync_TheWeekOfTheDemo_IsStoredMondayFirst_AndMarksTheHoursAsConfigured()
    {
        var saved = await Service().ReplaceHoursAsync(OrgA, DemoWeek());

        Assert.Equal(Instant.UtcDateTime, saved.ConfiguredAt);
        Assert.Equal(11, saved.Bands.Count);
        Assert.Equal(new SupplierWeeklyBand(DayOfWeek.Monday, 8 * 60, 13 * 60), saved.Bands[0]);
        Assert.Equal(new SupplierWeeklyBand(DayOfWeek.Saturday, 8 * 60, 14 * 60), saved.Bands[^1]);

        var read = await Service().GetHoursAsync(OrgA);
        Assert.Equal(saved.Bands, read.Bands);
        Assert.Equal(Instant.UtcDateTime, read.ConfiguredAt);
    }

    [Fact]
    public async Task ReplaceHoursAsync_ASecondSave_KeepsTheBandsThatStay_ChangesTheEndOfOneAndDropsTheOthers()
    {
        await Service().ReplaceHoursAsync(OrgA, Hours(Day(DayOfWeek.Monday, Band(8, 13), Band(14, 18)), Day(DayOfWeek.Tuesday, Band(8, 13))));
        var before = await RowsAsync(OrgA);
        var mondayMorning = before.Single(row => row.Weekday == DayOfWeek.Monday && row.StartMinute == 8 * 60);

        // Monday morning ends later, the afternoon and Tuesday are gone, Friday is new.
        await Service().ReplaceHoursAsync(OrgA, Hours(Day(DayOfWeek.Monday, Band(8, 14)), Day(DayOfWeek.Friday, Band(9, 12))));

        var after = await RowsAsync(OrgA);
        Assert.Equal(2, after.Count);
        var kept = after.Single(row => row.Weekday == DayOfWeek.Monday);
        Assert.Equal(mondayMorning.Id, kept.Id); // the same row, not a new one
        Assert.Equal(14 * 60, kept.EndMinute);
        Assert.Contains(after, row => row.Weekday == DayOfWeek.Friday && row.StartMinute == 9 * 60 && row.EndMinute == 12 * 60);
    }

    [Fact]
    public async Task ReplaceHoursAsync_AnEmptyWeek_ClearsTheHours_AndTheConfiguredMomentWithThem()
    {
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());

        var cleared = await Service().ReplaceHoursAsync(OrgA, new SupplierHoursInput([]));

        Assert.Empty(cleared.Bands);
        Assert.Null(cleared.ConfiguredAt);
        Assert.Empty(await RowsAsync(OrgA));
        Assert.Null((await Service().GetHoursAsync(OrgA)).ConfiguredAt);
    }

    [Fact]
    public async Task ReplaceHoursAsync_TheConfiguredMoment_FollowsTheLastSave()
    {
        await Service().ReplaceHoursAsync(OrgA, Hours(Day(DayOfWeek.Monday, Band(8, 13))));
        _clock.Advance(TimeSpan.FromDays(2));

        var second = await Service().ReplaceHoursAsync(OrgA, Hours(Day(DayOfWeek.Monday, Band(9, 13))));

        Assert.Equal(Instant.UtcDateTime.AddDays(2), second.ConfiguredAt);
    }

    [Fact]
    public async Task ReplaceHoursAsync_InvalidHours_ThrowNamingTheFields_AndLeaveTheStoredWeekAsItWas()
    {
        await Service().ReplaceHoursAsync(OrgA, Hours(Day(DayOfWeek.Monday, Band(8, 13))));

        var ex = await Assert.ThrowsAsync<SupplierAgendaRuleException>(
            () => Service().ReplaceHoursAsync(OrgA, Hours(Day(DayOfWeek.Monday, Band(8, 13), Band(12, 18)))));

        Assert.Equal(SupplierAgendaErrors.HoursInvalid, ex.Code);
        Assert.Equal(["days[0].bands[1]"], ex.Fields);
        var rows = await RowsAsync(OrgA);
        Assert.Equal(8 * 60, Assert.Single(rows).StartMinute);
        Assert.Equal(13 * 60, rows[0].EndMinute);
    }

    [Fact]
    public async Task ReplaceHoursAsync_ASupplier_NeverChangesTheHoursOfAnother()
    {
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());
        await Service().ReplaceHoursAsync(OrgB, Hours(Day(DayOfWeek.Sunday, Band(10, 12))));

        await Service().ReplaceHoursAsync(OrgB, new SupplierHoursInput([]));

        Assert.Equal(11, (await Service().GetHoursAsync(OrgA)).Bands.Count);
        Assert.Empty((await Service().GetHoursAsync(OrgB)).Bands);
    }

    [Fact]
    public async Task ReplaceHoursAsync_TheFirstWrite_CreatesTheSettingsRowWithTheDefaultRules()
    {
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());

        await using var db = CreateDb();
        var settings = await db.SupplierSettings.Where(s => s.OrgId == OrgA).SingleAsync();
        Assert.Equal(SupplierAgendaDefaults.BufferMinutes, settings.BufferMinutes);
        Assert.Equal(SupplierAgendaDefaults.MaxJobsPerDay, settings.MaxJobsPerDay);
        Assert.Equal(SupplierAgendaDefaults.MinNoticeHours, settings.MinNoticeHours);
        Assert.Equal(SupplierAgendaDefaults.HorizonDays, settings.HorizonDays);
        Assert.Equal(SupplierAgendaDefaults.SlotStepMinutes, settings.SlotStepMinutes);
        Assert.Equal(1, settings.ParallelJobs);
        Assert.Equal(180, settings.RespondWithinMinutes);
        Assert.False(settings.OnlineBookingEnabled);
        Assert.False(settings.AutoAcceptRegulars);
        Assert.Equal(Instant.UtcDateTime, settings.CreatedAt);
    }

    // ─── Time off ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddTimeOffAsync_AValidPeriod_IsStoredAndListed()
    {
        var entry = await Service().AddTimeOffAsync(
            OrgA, new SupplierTimeOffInput(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 2), SupplierTimeOffReason.Holiday, " Ponte "));

        Assert.Equal(OrgA, entry.OrgId);
        Assert.Equal("Ponte", entry.Label);
        Assert.Equal(Instant.UtcDateTime, entry.CreatedAt);

        var listed = Assert.Single(await Service().ListTimeOffAsync(OrgA));
        Assert.Equal(entry.Id, listed.Id);
        Assert.Equal(SupplierTimeOffReason.Holiday, listed.Reason);
    }

    [Fact]
    public async Task ListTimeOffAsync_OnlyWhatHasNotEnded_ByFirstDay_AndNeverTheOthersEntries()
    {
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7)); // over
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 26));
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 8)); // ends today: still counts
        await SeedTimeOffAsync(OrgB, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2));

        var listed = await Service().ListTimeOffAsync(OrgA);

        Assert.Equal(
            [new DateOnly(2026, 10, 6), new DateOnly(2026, 12, 24)],
            listed.Select(entry => entry.FromDate));
    }

    [Fact]
    public async Task AddTimeOffAsync_InvalidPeriod_Throws_AndStoresNothing()
    {
        var ex = await Assert.ThrowsAsync<SupplierAgendaRuleException>(
            () => Service().AddTimeOffAsync(OrgA, new SupplierTimeOffInput(Today.AddDays(5), Today, null, null)));

        Assert.Equal(SupplierAgendaErrors.TimeOffInvalid, ex.Code);
        Assert.Equal(["toDate"], ex.Fields);
        Assert.Empty(await Service().ListTimeOffAsync(OrgA));
    }

    [Fact]
    public async Task AddTimeOffAsync_TheLimitCountsWhatHasNotEnded_NotTheHistory()
    {
        for (var i = 0; i < SupplierAgendaLimits.MaxTimeOffEntries; i++)
            await SeedTimeOffAsync(OrgA, Today.AddDays(10 + i), Today.AddDays(10 + i));
        for (var i = 0; i < 5; i++)
            await SeedTimeOffAsync(OrgA, Today.AddDays(-30 - i), Today.AddDays(-30 - i)); // history: does not count
        await SeedTimeOffAsync(OrgB, Today.AddDays(10), Today.AddDays(10)); // another supplier: does not count

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().AddTimeOffAsync(OrgA, new SupplierTimeOffInput(Today.AddDays(200), Today.AddDays(200), null, null)));

        Assert.Equal(SupplierAgendaErrors.TimeOffLimitReached, ex.Code);
        Assert.Equal(SupplierAgendaLimits.MaxTimeOffEntries, ex.MessageArgs.Single());
        Assert.Equal(SupplierAgendaLimits.MaxTimeOffEntries, (await Service().ListTimeOffAsync(OrgA)).Count);

        // Another supplier is not stopped by it.
        await Service().AddTimeOffAsync(OrgB, new SupplierTimeOffInput(Today.AddDays(200), Today.AddDays(200), null, null));
    }

    [Fact]
    public async Task DeleteTimeOffAsync_RemovesItsDays_AndAnotherSuppliersEntryOrAMissingOneIs404()
    {
        var mine = await Service().AddTimeOffAsync(OrgA, new SupplierTimeOffInput(Today, Today.AddDays(2), null, null));
        var theirs = await Service().AddTimeOffAsync(OrgB, new SupplierTimeOffInput(Today, Today.AddDays(2), null, null));

        var other = await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteTimeOffAsync(OrgA, theirs.Id));
        Assert.Equal(SupplierAgendaErrors.TimeOffNotFound, other.Code);
        Assert.Equal("SupplierTimeOffNotFound", other.MessageKey);
        Assert.Single(await Service().ListTimeOffAsync(OrgB));

        await Service().DeleteTimeOffAsync(OrgA, mine.Id);

        Assert.Empty(await Service().ListTimeOffAsync(OrgA));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteTimeOffAsync(OrgA, mine.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteTimeOffAsync(OrgA, Guid.NewGuid()));
    }

    // ─── Blocks and extra openings ───────────────────────────────────────────────

    [Fact]
    public async Task AddBlockAsync_ABlockAndAnExtraOpening_AreStoredAsManualWindows()
    {
        var block = await Service().AddBlockAsync(
            OrgA, new SupplierBlockInput(SupplierBusyWindowKind.Block, Utc("2026-10-09T13:00:00Z"), Utc("2026-10-09T14:30:00Z"), " Dentista "));
        var extra = await Service().AddBlockAsync(
            OrgA, new SupplierBlockInput(SupplierBusyWindowKind.ExtraOpening, Utc("2026-10-10T13:00:00Z"), Utc("2026-10-10T16:00:00Z"), null));

        Assert.Equal(SupplierBusyWindowSource.Manual, block.Source);
        Assert.Equal(SupplierBusyWindowKind.Block, block.Kind);
        Assert.Equal("Dentista", block.Label);
        Assert.Null(block.ExternalUid);
        Assert.Equal(Instant.UtcDateTime, block.CreatedAt);
        Assert.Equal(SupplierBusyWindowKind.ExtraOpening, extra.Kind);
        Assert.Equal([block.Id, extra.Id], (await Service().ListBlocksAsync(OrgA)).Select(window => window.Id));
    }

    [Fact]
    public async Task ListBlocksAsync_OnlyManualWindowsThatHaveNotEnded_ByStart_NeverTheEngagementsOfTheCalendarFeedNorAnotherSuppliers()
    {
        var kept = await SeedWindowAsync(OrgA, "2026-10-12T08:00:00Z", "2026-10-12T09:00:00Z", SupplierBusyWindowKind.Block);
        var earlier = await SeedWindowAsync(OrgA, "2026-10-09T08:00:00Z", "2026-10-09T09:00:00Z", SupplierBusyWindowKind.ExtraOpening);
        await SeedWindowAsync(OrgA, "2026-10-01T08:00:00Z", "2026-10-01T09:00:00Z", SupplierBusyWindowKind.Block); // over
        await SeedWindowAsync(OrgA, "2026-10-12T10:00:00Z", "2026-10-12T11:00:00Z", SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed);
        await SeedWindowAsync(OrgB, "2026-10-12T08:00:00Z", "2026-10-12T09:00:00Z", SupplierBusyWindowKind.Block);

        var listed = await Service().ListBlocksAsync(OrgA);

        Assert.Equal([earlier.Id, kept.Id], listed.Select(window => window.Id));
    }

    [Fact]
    public async Task AddBlockAsync_InvalidBlock_Throws_AndStoresNothing()
    {
        var ex = await Assert.ThrowsAsync<SupplierAgendaRuleException>(
            () => Service().AddBlockAsync(
                OrgA, new SupplierBlockInput(SupplierBusyWindowKind.External, Utc("2026-10-09T13:00:00Z"), Utc("2026-10-09T14:00:00Z"), null)));

        Assert.Equal(SupplierAgendaErrors.BlockInvalid, ex.Code);
        Assert.Equal(["kind"], ex.Fields);
        Assert.Empty(await Service().ListBlocksAsync(OrgA));
    }

    [Fact]
    public async Task AddBlockAsync_TheLimitCountsTheWindowsThatHaveNotEnded_OfThatSupplierOnly()
    {
        for (var i = 0; i < SupplierAgendaLimits.MaxManualWindows; i++)
            await SeedWindowAsync(OrgA, $"2026-11-{1 + i % 28:00}T{8 + i / 28:00}:00:00Z", $"2026-11-{1 + i % 28:00}T{8 + i / 28:00}:30:00Z", SupplierBusyWindowKind.Block);
        await SeedWindowAsync(OrgA, "2026-09-01T08:00:00Z", "2026-09-01T09:00:00Z", SupplierBusyWindowKind.Block); // over: not counted
        await SeedWindowAsync(OrgA, "2026-10-12T10:00:00Z", "2026-10-12T11:00:00Z", SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed); // the feed's: not counted
        var input = new SupplierBlockInput(SupplierBusyWindowKind.Block, Utc("2026-12-01T08:00:00Z"), Utc("2026-12-01T09:00:00Z"), null);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service().AddBlockAsync(OrgA, input));

        Assert.Equal(SupplierAgendaErrors.BlockLimitReached, ex.Code);
        Assert.Equal(SupplierAgendaLimits.MaxManualWindows, ex.MessageArgs.Single());
        await Service().AddBlockAsync(OrgB, input); // another supplier is free to add
    }

    [Fact]
    public async Task DeleteBlockAsync_RemovesAManualWindow_AndAnEngagementOfTheFeedAnotherSuppliersOrAMissingOneIs404()
    {
        var mine = await SeedWindowAsync(OrgA, "2026-10-12T08:00:00Z", "2026-10-12T09:00:00Z", SupplierBusyWindowKind.Block);
        var feed = await SeedWindowAsync(OrgA, "2026-10-12T10:00:00Z", "2026-10-12T11:00:00Z", SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed);
        var theirs = await SeedWindowAsync(OrgB, "2026-10-12T08:00:00Z", "2026-10-12T09:00:00Z", SupplierBusyWindowKind.Block);

        var other = await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteBlockAsync(OrgA, theirs.Id));
        Assert.Equal(SupplierAgendaErrors.BlockNotFound, other.Code);
        Assert.Equal("SupplierBlockNotFound", other.MessageKey);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteBlockAsync(OrgA, feed.Id));

        await Service().DeleteBlockAsync(OrgA, mine.Id);

        Assert.Empty(await Service().ListBlocksAsync(OrgA));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteBlockAsync(OrgA, mine.Id));
        // The engagement of the feed and the other supplier's block are still there.
        await using var db = CreateDb();
        Assert.True(await db.SupplierBusyWindows.Where(w => w.OrgId == OrgA).AnyAsync(w => w.Id == feed.Id));
        Assert.True(await db.SupplierBusyWindows.Where(w => w.OrgId == OrgB).AnyAsync(w => w.Id == theirs.Id));
    }

    // ─── Rules ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRulesAsync_ASupplierThatNeverSavedAny_GetsTheDefaults_AndNothingIsWritten()
    {
        var rules = await Service().GetRulesAsync(OrgA);

        Assert.Equal(new SupplierPlanningRules(30, 3, 24, 35, 60, 1), rules);
        await using var db = CreateDb();
        Assert.False(await db.SupplierSettings.Where(s => s.OrgId == OrgA).AnyAsync());
    }

    [Fact]
    public async Task ReplaceRulesAsync_StoresTheFiveRules_AndReadsThemBack()
    {
        var saved = await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(15, 5, 12, 60, 30));

        Assert.Equal(new SupplierPlanningRules(15, 5, 12, 60, 30, 1), saved);
        Assert.Equal(saved, await Service().GetRulesAsync(OrgA));
        await using var db = CreateDb();
        var row = await db.SupplierSettings.Where(s => s.OrgId == OrgA).SingleAsync();
        Assert.Equal(Instant.UtcDateTime, row.UpdatedAt);
    }

    [Fact]
    public async Task ReplaceRulesAsync_LeavesTheOtherSettingsAsTheyAre_AndUpdatesTheSameRow()
    {
        await using (var seed = CreateDb())
        {
            seed.SupplierSettings.Add(new SupplierSettings
            {
                OrgId = OrgA,
                ParallelJobs = 2,
                RespondWithinMinutes = 120,
                OnlineBookingEnabled = true,
                AutoAcceptRegulars = true,
                NotifyNewRequests = false,
                HoursConfiguredAt = Instant.UtcDateTime.AddDays(-3),
            });
            await seed.SaveChangesAsync();
        }

        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(0, 8, 2, 90, 15));
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(45, 8, 2, 90, 15));

        await using var db = CreateDb();
        var row = await db.SupplierSettings.Where(s => s.OrgId == OrgA).SingleAsync(); // one row, not two
        Assert.Equal(45, row.BufferMinutes);
        Assert.Equal(2, row.ParallelJobs);
        Assert.Equal(120, row.RespondWithinMinutes);
        Assert.True(row.OnlineBookingEnabled);
        Assert.True(row.AutoAcceptRegulars);
        Assert.False(row.NotifyNewRequests);
        Assert.Equal(Instant.UtcDateTime.AddDays(-3), row.HoursConfiguredAt);
    }

    [Fact]
    public async Task ReplaceRulesAsync_InvalidRules_Throw_AndKeepTheStoredOnes()
    {
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(15, 5, 12, 60, 30));

        var ex = await Assert.ThrowsAsync<SupplierAgendaRuleException>(
            () => Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(15, 0, 12, 60, null)));

        Assert.Equal(SupplierAgendaErrors.RulesInvalid, ex.Code);
        Assert.Equal(["maxJobsPerDay", "slotStepMinutes"], ex.Fields);
        Assert.Equal(new SupplierPlanningRules(15, 5, 12, 60, 30, 1), await Service().GetRulesAsync(OrgA));
    }

    [Fact]
    public async Task ReplaceRulesAsync_ASupplier_NeverChangesTheRulesOfAnother()
    {
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(15, 5, 12, 60, 30));
        await Service().ReplaceRulesAsync(OrgB, new SupplierRulesInput(60, 1, 0, 7, 120));

        Assert.Equal(15, (await Service().GetRulesAsync(OrgA)).BufferMinutes);
        Assert.Equal(60, (await Service().GetRulesAsync(OrgB)).BufferMinutes);
    }

    [Fact]
    public async Task ReplaceHoursAsyncAndReplaceRulesAsync_ShareTheSettingsRow_NeitherEraseTheOther()
    {
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(15, 5, 12, 60, 30));
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());

        Assert.Equal(new SupplierPlanningRules(15, 5, 12, 60, 30, 1), await Service().GetRulesAsync(OrgA));
        Assert.NotNull((await Service().GetHoursAsync(OrgA)).ConfiguredAt);

        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(0, 5, 12, 60, 30));

        Assert.NotNull((await Service().GetHoursAsync(OrgA)).ConfiguredAt);
        await using var db = CreateDb();
        Assert.Equal(1, await db.SupplierSettings.Where(s => s.OrgId == OrgA).CountAsync());
    }

    // ─── Calendar ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCalendarAsync_PutsTogetherWhatTouchesTheRange_AndLeavesTheRestOut()
    {
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());
        // Time off: one inside, one that starts before the range and ends inside it, one that ends the day before it, one after.
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 15));
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 11));
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3));
        // Days: one closed by hand, one by the feed after the range, one open (not an override), one far away.
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 13), false, SupplierAvailabilitySource.Manual);
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 20), false, SupplierAvailabilitySource.ICalFeed);
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 16), true, SupplierAvailabilitySource.Manual);
        await SeedDayAsync(OrgA, new DateOnly(2026, 11, 20), false, SupplierAvailabilitySource.Manual);
        // The range starts at 22:00 UTC on the 11th (00:00 on the 12th in Rome). Windows: one inside, one that crosses that
        // instant, one that ends exactly at it, one of the feed, one after the range, one of another supplier.
        var inside = await SeedWindowAsync(OrgA, "2026-10-13T08:00:00Z", "2026-10-13T09:00:00Z", SupplierBusyWindowKind.Block);
        var crossing = await SeedWindowAsync(OrgA, "2026-10-11T21:00:00Z", "2026-10-11T23:00:00Z", SupplierBusyWindowKind.Block);
        var endsAtStart = await SeedWindowAsync(OrgA, "2026-10-11T20:00:00Z", "2026-10-11T22:00:00Z", SupplierBusyWindowKind.Block);
        var feed = await SeedWindowAsync(OrgA, "2026-10-14T08:00:00Z", "2026-10-14T09:00:00Z", SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed);
        await SeedWindowAsync(OrgA, "2026-10-25T08:00:00Z", "2026-10-25T09:00:00Z", SupplierBusyWindowKind.Block);
        await SeedWindowAsync(OrgB, "2026-10-13T08:00:00Z", "2026-10-13T09:00:00Z", SupplierBusyWindowKind.Block);

        var calendar = await Service().GetCalendarAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.Equal(new DateOnly(2026, 10, 12), calendar.From);
        Assert.Equal(new DateOnly(2026, 10, 18), calendar.To);
        Assert.Equal(11, calendar.WorkingHours.Count);
        Assert.Equal([new DateOnly(2026, 10, 13)], calendar.ClosedDays.Select(day => day.Date));
        Assert.Equal(
            [new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 14)],
            calendar.TimeOff.Select(entry => entry.FromDate));
        Assert.Equal([crossing.Id, inside.Id, feed.Id], calendar.Windows.Select(window => window.Id));
        Assert.DoesNotContain(calendar.Windows, window => window.Id == endsAtStart.Id);
        Assert.Empty(calendar.Requests);
    }

    [Fact]
    public async Task GetCalendarAsync_ClosedDays_AreOnlyTheOnesWithAvailableFalse_WithTheirSource()
    {
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 13), false, SupplierAvailabilitySource.Manual);
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 14), false, SupplierAvailabilitySource.ICalFeed);
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 15), true, SupplierAvailabilitySource.Manual);

        var calendar = await Service().GetCalendarAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.Equal(
            [
                new SupplierClosedDay(new DateOnly(2026, 10, 13), SupplierAvailabilitySource.Manual),
                new SupplierClosedDay(new DateOnly(2026, 10, 14), SupplierAvailabilitySource.ICalFeed),
            ],
            calendar.ClosedDays);
    }

    [Fact]
    public async Task GetCalendarAsync_ARangeOfTheMaximumLength_IsAccepted_OneDayMoreIsNot()
    {
        var from = new DateOnly(2026, 10, 1);

        var full = await Service().GetCalendarAsync(OrgA, from, from.AddDays(SupplierAgendaLimits.MaxCalendarDays - 1));
        Assert.Equal(from.AddDays(61), full.To);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Service().GetCalendarAsync(OrgA, from, from.AddDays(SupplierAgendaLimits.MaxCalendarDays)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().GetCalendarAsync(OrgA, from, from.AddDays(-1)));
    }

    [Fact]
    public async Task GetCalendarAsync_TheRequestsThatHaveADay_AreWholeDayItems_NeverTheOnesOfAnotherSupplierOrRejected()
    {
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        var requested = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto);
        var taken = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.PresoInCarico);
        var done = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Completato);
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Rifiutato);
        await SeedRequestAsync(world, OrgB, ServiceRequestStatus.Richiesto);
        var later = await SeedStayAsync(checkOut: new DateOnly(2026, 11, 30));
        await SeedRequestAsync(later, OrgA, ServiceRequestStatus.Richiesto); // outside the range

        var calendar = await Service().GetCalendarAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.Equal(3, calendar.Requests.Count);
        Assert.All(calendar.Requests, request => Assert.Equal(new DateOnly(2026, 10, 14), request.Date));
        Assert.Equal(
            new[] { requested.Id, taken.Id, done.Id }.Order(),
            calendar.Requests.Select(request => request.Id).Order());
        Assert.All(calendar.Requests, request => Assert.Equal("cleaning", request.Category));
        Assert.Contains(calendar.Requests, request => request.Status == ServiceRequestStatus.PresoInCarico);
    }

    // ─── The input of the planner, and the plan ──────────────────────────────────

    [Fact]
    public async Task BuildPlanningInputAsync_ASupplierThatNeverConfiguredAnything_HasTheDefaultRulesAndNoHours()
    {
        var input = await Service().BuildPlanningInputAsync(OrgA, Today, Today.AddDays(7));

        Assert.Equal(Instant.UtcDateTime, input.NowUtc);
        Assert.Equal(SupplierPlanningRules.Default, input.Rules);
        Assert.Empty(input.WeeklyHours);
        Assert.Empty(input.TimeOff);
        Assert.Empty(input.ClosedDays);
        Assert.Empty(input.ExtraOpenings);
        Assert.Empty(input.Occupancies);
    }

    [Fact]
    public async Task BuildPlanningInputAsync_ComposesTheRulesHoursTimeOffClosedDaysWindowsAndRequests()
    {
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(15, 2, 12, 60, 30));
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 15));
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 13), false, SupplierAvailabilitySource.Manual);
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 16), true, SupplierAvailabilitySource.Manual); // open: no override
        await SeedWindowAsync(OrgA, "2026-10-13T08:00:00Z", "2026-10-13T09:00:00Z", SupplierBusyWindowKind.Block);
        await SeedWindowAsync(OrgA, "2026-10-17T08:00:00Z", "2026-10-17T10:00:00Z", SupplierBusyWindowKind.ExtraOpening);
        await SeedWindowAsync(OrgA, "2026-10-13T12:00:00Z", "2026-10-13T13:00:00Z", SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed);
        await SeedWindowAsync(OrgB, "2026-10-13T08:00:00Z", "2026-10-13T09:00:00Z", SupplierBusyWindowKind.Block);
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto);
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.InCorso);
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Completato); // done: not a job any more
        await SeedRequestAsync(world, OrgB, ServiceRequestStatus.Richiesto);

        var input = await Service().BuildPlanningInputAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.Equal(new SupplierPlanningRules(15, 2, 12, 60, 30, 1), input.Rules);
        Assert.Equal(11, input.WeeklyHours.Count);
        Assert.Equal([new SupplierDateRange(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 15))], input.TimeOff);
        Assert.Equal([new DateOnly(2026, 10, 13)], input.ClosedDays);
        Assert.Equal([new SupplierInterval(Utc("2026-10-17T08:00:00Z"), Utc("2026-10-17T10:00:00Z"))], input.ExtraOpenings);

        Assert.Contains(input.Occupancies, o => o.Kind == SupplierOccupancyKind.Block && o.StartUtc == Utc("2026-10-13T08:00:00Z"));
        Assert.Contains(input.Occupancies, o => o.Kind == SupplierOccupancyKind.External && o.StartUtc == Utc("2026-10-13T12:00:00Z"));
        var jobs = input.Occupancies.Where(o => o.Kind == SupplierOccupancyKind.Request).ToList();
        Assert.Equal(2, jobs.Count); // the requested and the in-progress ones
        Assert.All(jobs, job =>
        {
            Assert.Equal(new DateOnly(2026, 10, 14), job.Day);
            Assert.False(job.HasInterval); // a request of today has a day, not hours
        });
        Assert.Equal(4, input.Occupancies.Count);
    }

    [Fact]
    public async Task BuildPlanningInputAsync_ARangeLongerThanThePlannerAccepts_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Service().BuildPlanningInputAsync(OrgA, Today, Today.AddDays(SupplierSlotPlanner.MaxRangeDays)));
    }

    [Fact]
    public async Task PlanAsync_TheRowsOfTheAgenda_BecomeTheSlotsAndTheClosuresOfTheDays()
    {
        // Now is Thursday 8 October 12:00 in Rome. Rules: no buffer, 2 jobs a day, no notice, step 60.
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(0, 2, 0, 35, 60));
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());
        await SeedTimeOffAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12)); // Monday: time off
        await SeedDayAsync(OrgA, new DateOnly(2026, 10, 13), false, SupplierAvailabilitySource.ICalFeed); // Tuesday: closed by the feed
        await SeedWindowAsync(OrgA, "2026-10-14T07:00:00Z", "2026-10-14T08:00:00Z", SupplierBusyWindowKind.Block); // Wednesday 09:00-10:00
        await SeedWindowAsync(OrgA, "2026-10-18T08:00:00Z", "2026-10-18T10:00:00Z", SupplierBusyWindowKind.ExtraOpening); // Sunday 10:00-12:00
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 15)); // Thursday: two jobs, the day is full
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto);
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.PresoInCarico);

        var plans = await Service().PlanAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18), new SupplierSlotQuery(60));

        Assert.Equal(7, plans.Count);
        Assert.Equal(SupplierDayClosure.TimeOff, plans[0].Closure); // Mon 12
        Assert.Equal(SupplierDayClosure.DayClosed, plans[1].Closure); // Tue 13
        Assert.Null(plans[2].Closure); // Wed 14: the block takes 09:00 only
        Assert.DoesNotContain(plans[2].Slots, slot => slot.StartUtc == Utc("2026-10-14T07:00:00Z"));
        Assert.Contains(plans[2].Slots, slot => slot.StartUtc == Utc("2026-10-14T06:00:00Z"));
        Assert.Equal(SupplierDayClosure.MaxJobsReached, plans[3].Closure); // Thu 15
        Assert.Null(plans[4].Closure); // Fri 16: 5 slots in the morning and 4 in the afternoon
        Assert.Equal(5 + 4, plans[4].Slots.Count);
        Assert.Null(plans[5].Closure); // Sat 17: 08:00-14:00
        Assert.Equal(6, plans[5].Slots.Count);
        Assert.Null(plans[6].Closure); // Sun 18: opened by the extra opening
        Assert.Equal(
            [Utc("2026-10-18T08:00:00Z"), Utc("2026-10-18T09:00:00Z")],
            plans[6].Slots.Select(slot => slot.StartUtc));
    }

    [Fact]
    public async Task PlanAsync_ASupplierWithoutHours_HasNoSlotAnywhere()
    {
        var plans = await Service().PlanAsync(OrgA, Today, Today.AddDays(6), new SupplierSlotQuery(60));

        Assert.All(plans, plan => Assert.True(plan.IsClosed));
        Assert.Equal(SupplierDayClosure.NoHours, plans[2].Closure);
    }

    // ─── SP-04: requests with a time ─────────────────────────────────────────────

    [Fact]
    public async Task BuildPlanningInputAsync_ARequestWithATimeOccupiesItsHours_AndOneWithoutOccupiesOnlyItsDay()
    {
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.PresoInCarico, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z"));
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto);

        var input = await Service().BuildPlanningInputAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        var jobs = input.Occupancies.Where(o => o.Kind == SupplierOccupancyKind.Request).ToList();
        Assert.Equal(2, jobs.Count);
        var timed = Assert.Single(jobs, job => job.HasInterval);
        Assert.Equal(Utc("2026-10-15T08:00:00Z"), timed.StartUtc);
        Assert.Equal(Utc("2026-10-15T10:00:00Z"), timed.EndUtc);
        Assert.True(timed.CountsTowardsDailyMax);
        var dated = Assert.Single(jobs, job => !job.HasInterval);
        Assert.Equal(new DateOnly(2026, 10, 14), dated.Day);
    }

    [Fact]
    public async Task BuildPlanningInputAsync_ATimedRequestCancelledOrRejectedOrDone_HoldsNothing()
    {
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        foreach (var status in new[] { ServiceRequestStatus.Annullato, ServiceRequestStatus.Rifiutato, ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato })
            await SeedRequestAsync(world, OrgA, status, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z"));

        var input = await Service().BuildPlanningInputAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.DoesNotContain(input.Occupancies, o => o.Kind == SupplierOccupancyKind.Request);
    }

    [Fact]
    public async Task BuildPlanningInputAsync_ATimedRequestOfAnotherSupplier_IsNotMine()
    {
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        await SeedRequestAsync(world, OrgB, ServiceRequestStatus.PresoInCarico, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z"));

        var input = await Service().BuildPlanningInputAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.Empty(input.Occupancies);
    }

    [Fact]
    public async Task BuildPlanningInputAsync_TheRequestBeingMoved_IsLeftOutSoItIsNotInItsOwnWay()
    {
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        var moving = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z"));
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.PresoInCarico, Utc("2026-10-15T12:00:00Z"), Utc("2026-10-15T14:00:00Z"));

        var all = await Service().BuildPlanningInputAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));
        var without = await Service().BuildPlanningInputAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18), moving.Id);

        Assert.Equal(2, all.Occupancies.Count);
        var left = Assert.Single(without.Occupancies);
        Assert.Equal(Utc("2026-10-15T12:00:00Z"), left.StartUtc);
    }

    [Fact]
    public async Task PlanAsync_ATimedRequest_TakesItsHoursAndTheBufferOutOfTheSlots()
    {
        // Thursday 15 October, hours 08-13 and 14-18, no notice, 30 minutes of buffer, step 60.
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(30, 3, 0, 35, 60));
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.PresoInCarico, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z")); // 10:00-12:00 in Rome

        var plan = Assert.Single(await Service().PlanAsync(OrgA, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15), new SupplierSlotQuery(120)));

        Assert.DoesNotContain(plan.Slots, slot => slot.StartUtc == Utc("2026-10-15T08:00:00Z"));
        // 08:00-10:00 in Rome ends exactly when the request starts, but not 30 minutes before it: the buffer takes it.
        Assert.DoesNotContain(plan.Slots, slot => slot.StartUtc == Utc("2026-10-15T06:00:00Z"));
        Assert.Contains(plan.Slots, slot => slot.StartUtc == Utc("2026-10-15T12:00:00Z")); // 14:00-16:00 in Rome
    }

    [Fact]
    public async Task PlanAsync_TheRequestBeingMoved_DoesNotTakeItsOwnSlot()
    {
        await Service().ReplaceRulesAsync(OrgA, new SupplierRulesInput(30, 3, 0, 35, 60));
        await Service().ReplaceHoursAsync(OrgA, DemoWeek());
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        var request = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z"));
        var query = new SupplierSlotQuery(120);

        var withIt = Assert.Single(await Service().PlanAsync(OrgA, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15), query));
        var without = Assert.Single(await Service().PlanAsync(OrgA, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15), query, request.Id));

        Assert.DoesNotContain(withIt.Slots, slot => slot.StartUtc == Utc("2026-10-15T08:00:00Z"));
        Assert.Contains(without.Slots, slot => slot.StartUtc == Utc("2026-10-15T08:00:00Z"));
    }

    [Fact]
    public async Task GetCalendarAsync_ARequestWithATime_CarriesItsHours_AndItsDayIsTheRomeDayOfItsStart()
    {
        var world = await SeedStayAsync(checkOut: new DateOnly(2026, 10, 14));
        // 23:30 UTC on the 15th is 01:30 on the 16th in Rome.
        var late = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.PresoInCarico, Utc("2026-10-15T23:30:00Z"), Utc("2026-10-16T01:30:00Z"));
        var plain = await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Richiesto);
        await SeedRequestAsync(world, OrgA, ServiceRequestStatus.Annullato, Utc("2026-10-15T08:00:00Z"), Utc("2026-10-15T10:00:00Z"));

        var calendar = await Service().GetCalendarAsync(OrgA, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18));

        Assert.Equal(2, calendar.Requests.Count);
        var timed = Assert.Single(calendar.Requests, request => request.Id == late.Id);
        Assert.True(timed.HasHours);
        Assert.Equal(new DateOnly(2026, 10, 16), timed.Date);
        Assert.Equal(Utc("2026-10-15T23:30:00Z"), timed.StartUtc);
        Assert.Equal(Utc("2026-10-16T01:30:00Z"), timed.EndUtc);
        var dated = Assert.Single(calendar.Requests, request => request.Id == plain.Id);
        Assert.False(dated.HasHours);
        Assert.Equal(new DateOnly(2026, 10, 14), dated.Date);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private SupplierAgendaService Service()
    {
        var db = CreateDb();
        return new SupplierAgendaService(db, new SupplierServiceRequestReader(db), NullLogger<SupplierAgendaService>.Instance, _clock);
    }

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task<List<SupplierWorkingHours>> RowsAsync(Guid orgId)
    {
        await using var db = CreateDb();
        return await db.SupplierWorkingHours.AsNoTracking().Where(h => h.OrgId == orgId).OrderBy(h => h.Weekday).ThenBy(h => h.StartMinute).ToListAsync();
    }

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>The week of the demo: Monday to Friday 08:00-13:00 and 14:00-18:00, Saturday 08:00-14:00.</summary>
    private static SupplierHoursInput DemoWeek() => Hours(
        Day(DayOfWeek.Monday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Tuesday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Wednesday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Thursday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Friday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Saturday, Band(8, 14)),
        Day(DayOfWeek.Sunday));

    private static SupplierHoursBandInput Band(int startHour, int endHour) => new(startHour * 60, endHour * 60);

    private static SupplierHoursDayInput Day(DayOfWeek weekday, params SupplierHoursBandInput[] bands) => new(weekday, bands);

    private static SupplierHoursInput Hours(params SupplierHoursDayInput[] days) => new(days);

    private async Task SeedTimeOffAsync(Guid orgId, DateOnly from, DateOnly to)
    {
        await using var db = CreateDb();
        db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = from, ToDate = to, CreatedAt = Instant.UtcDateTime });
        await db.SaveChangesAsync();
    }

    private async Task<SupplierBusyWindow> SeedWindowAsync(
        Guid orgId,
        string start,
        string end,
        SupplierBusyWindowKind kind,
        SupplierBusyWindowSource source = SupplierBusyWindowSource.Manual)
    {
        await using var db = CreateDb();
        var window = new SupplierBusyWindow
        {
            OrgId = orgId,
            StartUtc = Utc(start),
            EndUtc = Utc(end),
            Kind = kind,
            Source = source,
            ExternalUid = source == SupplierBusyWindowSource.ICalFeed ? $"uid-{Guid.NewGuid():N}" : null,
            CreatedAt = Instant.UtcDateTime,
        };
        db.SupplierBusyWindows.Add(window);
        await db.SaveChangesAsync();
        return window;
    }

    private async Task SeedDayAsync(Guid orgId, DateOnly date, bool available, SupplierAvailabilitySource source)
    {
        await using var db = CreateDb();
        db.SupplierAvailability.Add(new SupplierAvailability { OrgId = orgId, Date = date, Available = available, Source = source });
        await db.SaveChangesAsync();
    }

    /// <summary>A host with a property and a stay checking out on <paramref name="checkOut"/> (stored as UTC midnight, the convention of stay dates).</summary>
    private async Task<(Guid HostOrgId, Guid PropertyId, Guid BookingId)> SeedStayAsync(DateOnly checkOut)
    {
        await using var db = CreateDb();
        var host = new OrgEntity
        {
            Name = "Host Org",
            Slug = $"host-{Guid.NewGuid():N}"[..20],
            DisplayName = "Host Org",
            ContactEmail = "host@test.com",
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(host);
        var property = new Property
        {
            OwnerId = "auth0|host",
            OrgId = host.Id,
            Name = "Casa",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "H501",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            CinCode = "IT058091C27G5FFZDZ",
        };
        db.Properties.Add(property);
        var booking = new Booking
        {
            OrgId = host.Id,
            PropertyId = property.Id,
            GuestId = Guid.NewGuid(),
            CheckInDate = checkOut.AddDays(-3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckOutDate = checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (host.Id, property.Id, booking.Id);
    }

    private async Task<ServiceRequest> SeedRequestAsync(
        (Guid HostOrgId, Guid PropertyId, Guid BookingId) world,
        Guid supplierOrgId,
        ServiceRequestStatus status,
        DateTime? start = null,
        DateTime? end = null)
    {
        await using var db = CreateDb();
        var request = new ServiceRequest
        {
            OrgId = world.HostOrgId,
            BookingId = world.BookingId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            PropertyId = world.PropertyId,
            SupplierOrgId = supplierOrgId,
            Category = ServiceCategories.Cleaning,
            Status = status,
            ScheduledStartUtc = start,
            ScheduledEndUtc = end,
            CreatedAt = Instant.UtcDateTime,
            UpdatedAt = Instant.UtcDateTime,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }
}
