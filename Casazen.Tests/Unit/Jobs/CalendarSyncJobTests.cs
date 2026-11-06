using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// The calendar sync (E02/F02/US01/T03, ADR-002, NF-36): the nights Octorate holds closed become blocks of the property, once and without
/// duplicating what CasaZen already knows; the read is inbound only; the iCal feeds of the channels of the listing are deprecated after a
/// successful read and never on a read that failed or could not be understood; every org is read under its own scope and writes its own rows.
/// </summary>
public class CalendarSyncJobTests
{
    // ─── What becomes a block ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachRunOfClosedNights_IsOneBlockOfTheProperty_WithTheKeyOfItsFirstNight_AndTheWindowIsOneYearFromToday()
    {
        var h = new Octorate.BookingImportHarness();
        Serve(h, Open(8), Closed(9), Closed(10), Open(11), Closed(12), Closed(13), NeverOpened(20), Stopped(25));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        // 9 and 10 are one block, 12 and 13 another, 25 is stopped (a block of one night); 20 was never opened: it closes nothing.
        var blocks = await OctorateBlocksAsync(h);
        Assert.Equal(["octorate:2026-11-09", "octorate:2026-11-12", "octorate:2026-11-25"], blocks.Select(b => b.ExternalUid));
        Assert.Equal([Utc(9), Utc(12), Utc(25)], blocks.Select(b => b.StartUtc));
        Assert.Equal([Utc(11), Utc(14), Utc(26)], blocks.Select(b => b.EndUtc));
        Assert.All(blocks, b =>
        {
            Assert.Equal(CalendarBlockSource.Octorate, b.Source);
            Assert.Equal(h.PropertyId, b.PropertyId);
            Assert.Equal(OrgA, b.OrgId);
            Assert.Null(b.FeedId);
            Assert.Null(b.Currency);
            Assert.Equal("Octorate", b.Summary);
            Assert.Equal(Now.UtcDateTime, b.LastSyncedAt);
        });

        // One call for the room the mapping chose, from today to a year after it (the last day included), and nothing written to Octorate.
        h.Client.Verify(
            c => c.GetCalendarAsync(
                Accommodation,
                It.Is<IReadOnlyCollection<string>>(p => p.Single() == Room),
                new DateOnly(2026, 11, 6),
                new DateOnly(2027, 11, 6),
                It.IsAny<CancellationToken>()),
            Times.Once);
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ASecondRun_ChangesNothingAndWritesNothing_TheRowsKeepTheirIds()
    {
        var h = new Octorate.BookingImportHarness();
        Serve(h, Closed(9), Closed(10), Open(11), Closed(20));
        await using var db = h.NewDb();
        var job = Job(h, db);

        await job.ExecuteAsync(CancellationToken.None);
        var first = await OctorateBlocksAsync(h);
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        await job.ExecuteAsync(CancellationToken.None);
        var second = await OctorateBlocksAsync(h);

        Assert.Equal(2, second.Count);
        Assert.Equal(first.Select(b => b.Id), second.Select(b => b.Id));
        // A row that did not change is not written: its time is the one of its last change.
        Assert.Equal(first.Select(b => b.LastSyncedAt), second.Select(b => b.LastSyncedAt));
    }

    [Fact]
    public async Task WhenOctorateReopensOrClosesMoreNights_TheBlockFollows_AndABlockWithNoClosedNightLeftIsRemoved()
    {
        var h = new Octorate.BookingImportHarness();
        await using var db = h.NewDb();
        var job = Job(h, db);

        Serve(h, Closed(9), Closed(10), Closed(11), Closed(20));
        await job.ExecuteAsync(CancellationToken.None);
        var first = (await OctorateBlocksAsync(h)).ToDictionary(b => b.ExternalUid!);

        // 11 is open again, 21 is closed: the first block shrinks, the second grows, each keeps its row.
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        Serve(h, Closed(9), Closed(10), Open(11), Closed(20), Closed(21));
        await job.ExecuteAsync(CancellationToken.None);
        var second = (await OctorateBlocksAsync(h)).ToDictionary(b => b.ExternalUid!);

        Assert.Equal(2, second.Count);
        Assert.Equal(first["octorate:2026-11-09"].Id, second["octorate:2026-11-09"].Id);
        Assert.Equal(Utc(11), second["octorate:2026-11-09"].EndUtc);
        Assert.Equal(first["octorate:2026-11-20"].Id, second["octorate:2026-11-20"].Id);
        Assert.Equal(Utc(22), second["octorate:2026-11-20"].EndUtc);
        Assert.Equal(Now.UtcDateTime.AddMinutes(15), second["octorate:2026-11-20"].LastSyncedAt);

        // Everything is open: no block left.
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        Serve(h, Open(9), Open(10), Open(20));
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Empty(await OctorateBlocksAsync(h));
    }

    [Fact]
    public async Task ANightThatABookingAManualBlockOrAnICalBlockTakes_IsNotBlockedASecondTime()
    {
        var h = new Octorate.BookingImportHarness();
        // A booking (the reservation of a channel, or one CasaZen sent to Octorate) on 20 and 21; a manual block on 25 and 26; an iCal block on 14.
        await h.SeedBookingAsync("7001");
        await SeedBlockAsync(h, CalendarBlockSource.Manual, firstNight: 25, checkOutDay: 27);
        await SeedBlockAsync(h, CalendarBlockSource.ICalImport, firstNight: 14, checkOutDay: 15, feedId: await SeedFeedAsync(h, ICalFeedChannel.Airbnb));
        Serve(h, Closed(14), Closed(19), Closed(20), Closed(21), Closed(22), Closed(23), Closed(24), Closed(25), Closed(26), Closed(27), Closed(28));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        // Only the nights nothing else takes: 19, 22 to 24, 27 and 28. The block of the manual source and the booking are as they were.
        var blocks = await OctorateBlocksAsync(h);
        Assert.Equal([Utc(19), Utc(22), Utc(27)], blocks.Select(b => b.StartUtc));
        Assert.Equal([Utc(20), Utc(25), Utc(29)], blocks.Select(b => b.EndUtc));
        await using var read = h.NewDb();
        Assert.Equal(1, await read.CalendarBlocks.CountAsync(b => b.Source == CalendarBlockSource.Manual));
        Assert.Equal(1, await read.CalendarBlocks.CountAsync(b => b.Source == CalendarBlockSource.ICalImport));
        Assert.Equal(1, await read.Bookings.CountAsync());
    }

    [Fact]
    public async Task ACancelledBooking_TakesNoNight_AndNeitherDoesABookingOfAnotherPropertyOrOrg()
    {
        var h = new Octorate.BookingImportHarness();
        await h.SeedBookingAsync("7001", status: BookingStatus.Cancelled);
        await h.SeedBookingAsync("7002", propertyId: Guid.NewGuid());
        await h.SeedBookingAsync("7003", orgId: OrgB);
        Serve(h, Closed(20), Closed(21));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        var block = Assert.Single(await OctorateBlocksAsync(h));
        Assert.Equal(Utc(20), block.StartUtc);
        Assert.Equal(Utc(22), block.EndUtc);
    }

    [Fact]
    public async Task TheBlocksOfTheOctorateCalendar_TakeTheirNights_LikeEveryOtherBlock()
    {
        var h = new Octorate.BookingImportHarness();
        Serve(h, Closed(20), Closed(21));
        await using var db = h.NewDb();
        await Job(h, db).ExecuteAsync(CancellationToken.None);

        // The one rule of the occupancy (availability of the booking site, checks of a booking): the nights 20 and 21 are taken, the 22nd is not.
        await using var read = h.NewDb();
        Assert.True(await read.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(h.PropertyId, Utc(21), Utc(22))));
        Assert.False(await read.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(h.PropertyId, Utc(22), Utc(23))));
    }

    // ─── The window follows the clock of the property ────────────────────────────────────────────────────

    [Fact]
    public async Task TodayIsTheDayOnTheClockOfTheProperty_AndRomeWhenTheZoneIsNotOneTheServerKnows()
    {
        // 12:00 UTC on 6 November is already the 7th in Auckland (UTC+13), and the 6th in Rome.
        var auckland = new Octorate.BookingImportHarness(timezone: "Pacific/Auckland");
        auckland.Clock.Advance(TimeSpan.FromHours(4));
        Serve(auckland, Closed(10));
        await using var aucklandDb = auckland.NewDb();
        await Job(auckland, aucklandDb).ExecuteAsync(CancellationToken.None);
        auckland.Client.Verify(
            c => c.GetCalendarAsync(
                Accommodation, It.IsAny<IReadOnlyCollection<string>>(), new DateOnly(2026, 11, 7), new DateOnly(2027, 11, 7), It.IsAny<CancellationToken>()),
            Times.Once);

        var unknown = new Octorate.BookingImportHarness(timezone: "Not/AZone");
        unknown.Clock.Advance(TimeSpan.FromHours(4));
        Serve(unknown, Closed(10));
        await using var unknownDb = unknown.NewDb();
        await Job(unknown, unknownDb).ExecuteAsync(CancellationToken.None);
        unknown.Client.Verify(
            c => c.GetCalendarAsync(
                Accommodation, It.IsAny<IReadOnlyCollection<string>>(), new DateOnly(2026, 11, 6), new DateOnly(2027, 11, 6), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── What changes nothing ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedRead_LeavesTheBlocksAndTheFeedsAsTheyWere_AndTheOtherPropertiesAreStillRead()
    {
        var h = new Octorate.BookingImportHarness();
        var otherProperty = await h.AddListingAsync("111111", "222222");
        await SetChannelsAsync(h, """{ "274": "A-1", "142": "B-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        await SeedBlockAsync(h, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);

        // The room of the first property cannot be read; the other property's can.
        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        ServeFor(h, "111111", "222222", Closed(15, "222222"));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        // Nothing changed for the property that failed: its block is there, the feed of its channel is still imported.
        await using var read = h.NewDb();
        var kept = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == h.PropertyId);
        Assert.Equal(Utc(9), kept.StartUtc);
        Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
        // The other property was read all the same.
        var other = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == otherProperty);
        Assert.Equal(Utc(15), other.StartUtc);
    }

    [Fact]
    public async Task AnOrgWhoseConnectionIsRefused_IsLeftForTheNextRun_TheRestOfTheOrgIsNotRead()
    {
        var h = new Octorate.BookingImportHarness();
        await h.AddListingAsync("111111", "222222");
        Serve(h, Closed(10));
        h.Client
            .Setup(c => c.GetCalendarAsync("111111", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.Refused(403));
        await using var db = h.NewDb();

        // "111111" is read first (by accommodation id) and refused: the other listing of the org is not asked for.
        await Job(h, db).ExecuteAsync(CancellationToken.None);

        h.Client.Verify(
            c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(await OctorateBlocksAsync(h));
    }

    [Fact]
    public async Task AnAnswerWithoutADayOfTheRoom_ChangesNothing_AndDeprecatesNothing()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        await SeedBlockAsync(h, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);
        // Two units, neither of them the room of the listing: the days are nobody's.
        Serve(h, Closed(15, "900001"), Closed(15, "900002"));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        await using var read = h.NewDb();
        Assert.Equal(Utc(9), (await read.CalendarBlocks.AsNoTracking().SingleAsync()).StartUtc);
        Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
    }

    [Fact]
    public async Task ACalendarNobodyOpened_IsNoProof_TheBlocksStayAndTheFeedsKeepBeingImported_UntilADayIsOpened()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        await SeedBlockAsync(h, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);
        // Every day has no unit, no price and is not stopped: a structure that was never filled, or that cannot be written for lack of a licence.
        Serve(h, NeverOpened(9), NeverOpened(10), NeverOpened(11));
        await using var db = h.NewDb();
        var job = Job(h, db);

        await job.ExecuteAsync(CancellationToken.None);

        await using (var read = h.NewDb())
        {
            Assert.Equal(Utc(9), (await read.CalendarBlocks.AsNoTracking().SingleAsync()).StartUtc);
            Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
        }

        // The first day that is opened makes it a live calendar: the read counts, the nights that are open go and the feed is handed over.
        Serve(h, NeverOpened(9), Open(10), NeverOpened(11));
        await job.ExecuteAsync(CancellationToken.None);

        await using var after = h.NewDb();
        Assert.Empty(await OctorateBlocksAsync(h));
        Assert.True((await after.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
    }

    [Fact]
    public async Task OneUnitOnly_NamedByAnotherIdThanTheOneAskedFor_IsTheRoomOfTheListing()
    {
        var h = new Octorate.BookingImportHarness();
        Serve(h, Closed(15, "900001"));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        Assert.Equal(Utc(15), Assert.Single(await OctorateBlocksAsync(h)).StartUtc);
    }

    [Fact]
    public async Task OnlyTheListingsThatAreConfirmedWithTheirRoom_OfAConnectedOrg_AreRead()
    {
        var disconnected = new Octorate.BookingImportHarness();
        await using (var db = disconnected.NewDb())
        {
            (await db.OctorateConnections.SingleAsync()).Status = OctorateConnectionStatus.Disconnected;
            await db.SaveChangesAsync();
        }

        var noRoom = new Octorate.BookingImportHarness(room: null);
        var proposed = new Octorate.BookingImportHarness(status: OctorateMappingStatus.Proposed);

        foreach (var h in new[] { disconnected, noRoom, proposed })
        {
            Serve(h, Closed(10));
            await using var db = h.NewDb();
            await Job(h, db).ExecuteAsync(CancellationToken.None);
            h.Client.VerifyNoOtherCalls();
            Assert.Empty(await OctorateBlocksAsync(h));
        }
    }

    // ─── The hand-over of the iCal feeds (ADR-002-R11, R14, NF-28) ───────────────────────────────────────

    [Fact]
    public async Task AfterASuccessfulRead_TheFeedsOfTheChannelsOfTheListingAreDeprecated_AFeedOfChannelOtherIsNot()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1", "142": "B-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        var booking = await SeedFeedAsync(h, ICalFeedChannel.BookingCom);
        var other = await SeedFeedAsync(h, ICalFeedChannel.Other);
        // Nothing is closed: the read worked all the same.
        Serve(h, Open(10), Open(11));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        await using var read = h.NewDb();
        var feeds = await read.PropertyICalFeeds.AsNoTracking().ToDictionaryAsync(f => f.Id);
        Assert.True(feeds[airbnb].Deprecated);
        Assert.True(feeds[booking].Deprecated);
        Assert.Equal(Now.UtcDateTime, feeds[airbnb].DeprecatedAt);
        Assert.False(feeds[other].Deprecated);
        Assert.Null(feeds[other].DeprecatedAt);
    }

    [Fact]
    public async Task BeforeAnyRead_AndWhileTheMappingIsNotConfirmed_TheFeedsKeepBeingImported()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);

        // Nothing has been read yet: the feed is imported.
        await using (var read = h.NewDb())
            Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);

        var proposed = new Octorate.BookingImportHarness(status: OctorateMappingStatus.Proposed);
        await SetChannelsAsync(proposed, """{ "274": "A-1" }""");
        var proposedFeed = await SeedFeedAsync(proposed, ICalFeedChannel.Airbnb);
        Serve(proposed, Open(10));
        await using var db = proposed.NewDb();
        await Job(proposed, db).ExecuteAsync(CancellationToken.None);

        await using var after = proposed.NewDb();
        Assert.False((await after.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == proposedFeed)).Deprecated);
    }

    // ─── The blocks of a property that is no longer mapped ───────────────────────────────────────────────

    [Fact]
    public async Task TheBlocksOfAPropertyWithNoConfirmedMapping_AreRemoved_ButNotThoseOfAPropertyWhoseConnectionIsOnlyDisconnected()
    {
        // The host rejected the listing: its closures no longer belong to the property.
        var rejected = new Octorate.BookingImportHarness(status: OctorateMappingStatus.Rejected);
        await SeedBlockAsync(rejected, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);
        await SeedBlockAsync(rejected, CalendarBlockSource.Manual, firstNight: 20, checkOutDay: 22);
        await using (var db = rejected.NewDb())
            await Job(rejected, db).ExecuteAsync(CancellationToken.None);

        await using (var read = rejected.NewDb())
        {
            var left = await read.CalendarBlocks.AsNoTracking().ToListAsync();
            Assert.Equal(CalendarBlockSource.Manual, Assert.Single(left).Source);
        }

        // The mapping is confirmed but the connection is disconnected (or its token was refused): the nights stay closed until it is back.
        var disconnected = new Octorate.BookingImportHarness();
        await SeedBlockAsync(disconnected, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);
        await using (var db = disconnected.NewDb())
        {
            (await db.OctorateConnections.SingleAsync()).Status = OctorateConnectionStatus.Disconnected;
            await db.SaveChangesAsync();
            await Job(disconnected, db).ExecuteAsync(CancellationToken.None);
        }

        Assert.Single(await OctorateBlocksAsync(disconnected));
        disconnected.Client.VerifyNoOtherCalls();
    }

    // ─── Tenant isolation ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachOrgIsReadUnderItsOwnScope_AndWritesItsOwnRowsOnly()
    {
        var h = new Octorate.BookingImportHarness();
        var propertyB = await SeedOrgBListingAsync(h);
        var scope = h.Scope();
        var seen = new Dictionary<string, Guid>();
        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                seen[Accommodation] = scope.OrgId;
                return Task.FromResult(new[] { Closed(10) });
            });
        h.Client
            .Setup(c => c.GetCalendarAsync("B-ACC", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                seen["B-ACC"] = scope.OrgId;
                return Task.FromResult(new[] { Closed(15, "B-ROOM") });
            });
        await using var db = h.NewDb();

        await Job(h, db, scope).ExecuteAsync(CancellationToken.None);

        // The client takes no org: the job opened the scope of the org of each property for its call (and the scope is closed after).
        Assert.Equal(OrgA, seen[Accommodation]);
        Assert.Equal(OrgB, seen["B-ACC"]);
        Assert.Throws<InvalidOperationException>(() => scope.OrgId);

        await using var read = h.NewDb();
        var blockA = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == h.PropertyId);
        var blockB = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == propertyB);
        Assert.Equal((OrgA, Utc(10)), (blockA.OrgId, blockA.StartUtc));
        Assert.Equal((OrgB, Utc(15)), (blockB.OrgId, blockB.StartUtc));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static CalendarSyncJob Job(Octorate.BookingImportHarness h, AppDbContext db, OctorateOrgScope? scope = null) =>
        new(
            db,
            h.Client.Object,
            scope ?? h.Scope(),
            new ICalMigrationService(db, h.Clock, NullLogger<ICalMigrationService>.Instance),
            StagingHost(),
            h.Clock,
            NullLogger<CalendarSyncJob>.Instance);

    private static IHostEnvironment StagingHost()
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(e => e.EnvironmentName).Returns("Staging");
        return host.Object;
    }

    /// <summary>The calendar of the room of the harness answers these days (the last answer set is the one given).</summary>
    private static void Serve(Octorate.BookingImportHarness h, params OctorateCalendarDay[] days)
    {
        h.Client.Reset();
        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(days);
    }

    private static void ServeFor(Octorate.BookingImportHarness h, string accommodation, string room, params OctorateCalendarDay[] days) =>
        h.Client
            .Setup(c => c.GetCalendarAsync(accommodation, It.Is<IReadOnlyCollection<string>>(p => p.Single() == room), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(days);

    private static DateTime Utc(int day) => new(2026, 11, day, 0, 0, 0, DateTimeKind.Utc);

    private static OctorateCalendarDay Closed(int day, string product = Room) =>
        new(product, new DateOnly(2026, 11, day), 0, 100m, false, null, null, null, null);

    private static OctorateCalendarDay Open(int day) =>
        new(Room, new DateOnly(2026, 11, day), 1, 100m, false, 2, null, null, null);

    private static OctorateCalendarDay Stopped(int day) =>
        new(Room, new DateOnly(2026, 11, day), 1, 100m, true, null, null, null, null);

    private static OctorateCalendarDay NeverOpened(int day) =>
        new(Room, new DateOnly(2026, 11, day), 0, 0m, false, null, null, null, null);

    private static async Task<List<CalendarBlock>> OctorateBlocksAsync(Octorate.BookingImportHarness h)
    {
        await using var db = h.NewDb();
        return await db.CalendarBlocks.AsNoTracking().Where(b => b.Source == CalendarBlockSource.Octorate).OrderBy(b => b.StartUtc).ToListAsync();
    }

    private static async Task SeedBlockAsync(
        Octorate.BookingImportHarness h,
        CalendarBlockSource source,
        int firstNight,
        int checkOutDay,
        Guid? feedId = null)
    {
        await using var db = h.NewDb();
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = h.PropertyId,
            OrgId = OrgA,
            Source = source,
            FeedId = feedId,
            ExternalUid = source == CalendarBlockSource.Manual ? null : $"seed-{firstNight}",
            StartUtc = Utc(firstNight),
            EndUtc = Utc(checkOutDay),
            ManualReason = source == CalendarBlockSource.Manual ? CalendarBlockReason.Owner : null,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedFeedAsync(Octorate.BookingImportHarness h, ICalFeedChannel channel)
    {
        await using var db = h.NewDb();
        var feed = new PropertyICalFeed
        {
            PropertyId = h.PropertyId,
            OrgId = OrgA,
            Channel = channel,
            ImportUrl = "https://example.com/cal.ics",
        };
        db.PropertyICalFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.Id;
    }

    private static async Task SetChannelsAsync(Octorate.BookingImportHarness h, string channelIdsJson)
    {
        await using var db = h.NewDb();
        (await db.OctorateListingMappings.SingleAsync(m => m.Id == h.MappingId)).ChannelIdsJson = channelIdsJson;
        await db.SaveChangesAsync();
    }

    /// <summary>A property of org B with a confirmed listing of its own ("B-ACC", room "B-ROOM") on a connection of org B.</summary>
    private static async Task<Guid> SeedOrgBListingAsync(Octorate.BookingImportHarness h)
    {
        await using var db = h.NewDb();
        var connection = new OctorateConnection { OrgId = OrgB, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        var property = new Property
        {
            OrgId = OrgB,
            OwnerId = "auth0|owner-b",
            Name = "Casa del Mare",
            Address = "Via del Mare 2",
            City = "Roma",
            MaxGuests = 2,
        };
        db.OctorateConnections.Add(connection);
        db.Properties.Add(property);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgB,
            ConnectionId = connection.Id,
            OctorateAccommodationId = "B-ACC",
            OctorateProductId = "B-ROOM",
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        return property.Id;
    }
}
