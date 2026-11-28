using System.Text.Json;
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
    [Fact]
    public async Task EachRunOfClosedNights_IsOneBlockOfTheProperty_WithTheKeyOfItsFirstNight_AndTheWindowIsOneYearFromToday()
    {
        var h = new Octorate.BookingImportHarness();
        Serve(h, Open(8), Closed(9), Closed(10), Open(11), Closed(12), Closed(13), NeverOpened(20), Stopped(25));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

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

        h.Clock.Advance(TimeSpan.FromMinutes(15));
        Serve(h, Open(9), Open(10), Open(20));
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Empty(await OctorateBlocksAsync(h));
    }

    [Fact]
    public async Task ANightThatABookingAManualBlockOrAnICalBlockTakes_IsNotBlockedASecondTime()
    {
        var h = new Octorate.BookingImportHarness();
        await h.SeedBookingAsync("7001");
        await SeedBlockAsync(h, CalendarBlockSource.Manual, firstNight: 25, checkOutDay: 27);
        await SeedBlockAsync(h, CalendarBlockSource.ICalImport, firstNight: 14, checkOutDay: 15, feedId: await SeedFeedAsync(h, ICalFeedChannel.Airbnb));
        Serve(h, Closed(14), Closed(19), Closed(20), Closed(21), Closed(22), Closed(23), Closed(24), Closed(25), Closed(26), Closed(27), Closed(28));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

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

        await using var read = h.NewDb();
        Assert.True(await read.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(h.PropertyId, Utc(21), Utc(22))));
        Assert.False(await read.CalendarBlocks.AnyAsync(PropertyOccupancy.BlockTakesNightIn(h.PropertyId, Utc(22), Utc(23))));
    }

    [Fact]
    public async Task TodayIsTheDayOnTheClockOfTheProperty_AndRomeWhenTheZoneIsNotOneTheServerKnows()
    {
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

    [Fact]
    public async Task AFailedRead_LeavesTheBlocksAndTheFeedsAsTheyWere_AndTheOtherPropertiesAreStillRead()
    {
        var h = new Octorate.BookingImportHarness();
        var otherProperty = await h.AddListingAsync("111111", "222222");
        await SetChannelsAsync(h, """{ "274": "A-1", "142": "B-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        await SeedBlockAsync(h, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);

        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        ServeFor(h, "111111", "222222", Closed(15, "222222"));
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        await using var read = h.NewDb();
        var kept = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == h.PropertyId);
        Assert.Equal(Utc(9), kept.StartUtc);
        Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
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
        Serve(h, NeverOpened(9), NeverOpened(10), NeverOpened(11));
        await using var db = h.NewDb();
        var job = Job(h, db);

        await job.ExecuteAsync(CancellationToken.None);

        await using (var read = h.NewDb())
        {
            Assert.Equal(Utc(9), (await read.CalendarBlocks.AsNoTracking().SingleAsync()).StartUtc);
            Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
        }

        Serve(h, NeverOpened(9), Open(10), NeverOpened(11));
        await job.ExecuteAsync(CancellationToken.None);

        await using var after = h.NewDb();
        Assert.Empty(await OctorateBlocksAsync(h));
        Assert.True((await after.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
    }

    [Fact]
    public async Task DaysThatSayNothingButTheirDate_AreNoReadingOfTheCalendar_TheBlocksStayAndNoFeedIsDeprecated()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        await SeedBlockAsync(h, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);
        using var document = JsonDocument.Parse("""
            { "data": [ { "id": "956528", "days": [ { "date": "2026-11-09" }, { "date": "2026-11-10" }, { "date": "2026-11-11" } ] } ] }
            """);
        var days = OctorateCalendarReader.Read(document.RootElement, [Room]);
        Assert.Equal(3, days.Count);
        Assert.All(days, d => Assert.False(d.HasSignal));
        Serve(h, [.. days]);
        await using var db = h.NewDb();

        await Job(h, db).ExecuteAsync(CancellationToken.None);

        await AssertUntouchedAsync(h, airbnb);
    }

    [Fact]
    public async Task AnAnswerThatDoesNotReachBothEndsOfTheWindow_ChangesNothing_AndDeprecatesNothing_UntilItIsComplete()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        await SeedBlockAsync(h, CalendarBlockSource.Octorate, firstNight: 9, checkOutDay: 11);
        await using var db = h.NewDb();
        var job = Job(h, db);
        var firstDay = new DateOnly(2026, 11, 6);
        var lastDay = new DateOnly(2027, 11, 6);

        ServeAsIs(h, OpenOn(firstDay), Open(7), Open(8), Open(12));
        await job.ExecuteAsync(CancellationToken.None);
        await AssertUntouchedAsync(h, airbnb);

        ServeAsIs(h, Open(7), Open(8), Open(12), OpenOn(lastDay));
        await job.ExecuteAsync(CancellationToken.None);
        await AssertUntouchedAsync(h, airbnb);

        ServeAsIs(h, OpenOn(firstDay), Open(9), Open(10), Closed(12), OpenOn(lastDay));
        await job.ExecuteAsync(CancellationToken.None);

        await using var read = h.NewDb();
        Assert.Equal(["octorate:2026-11-12"], (await read.CalendarBlocks.AsNoTracking().ToListAsync()).Select(b => b.ExternalUid));
        Assert.True((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == airbnb)).Deprecated);
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

    [Fact]
    public async Task AfterASuccessfulRead_TheFeedsOfTheChannelsOfTheListingAreDeprecated_AFeedOfChannelOtherIsNot()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1", "142": "B-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        var booking = await SeedFeedAsync(h, ICalFeedChannel.BookingCom);
        var other = await SeedFeedAsync(h, ICalFeedChannel.Other);
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

    [Fact]
    public async Task WhenTheListingIsNoLongerRead_TheDeprecatedFeedsAreImportedAgain_AndHandedOverAgainWhenItIsBack()
    {
        var h = new Octorate.BookingImportHarness();
        await SetChannelsAsync(h, """{ "274": "A-1", "142": "B-1" }""");
        var airbnb = await SeedFeedAsync(h, ICalFeedChannel.Airbnb);
        Serve(h, Open(10), Open(11));
        await using var db = h.NewDb();
        var job = Job(h, db);

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);
        await AssertFeedAsync(h, airbnb, deprecated: true);

        await SetConnectionAsync(h, OctorateConnectionStatus.Disconnected);
        await job.ExecuteAsync(CancellationToken.None);
        await AssertFeedAsync(h, airbnb, deprecated: false);

        await SetConnectionAsync(h, OctorateConnectionStatus.Connected);
        await job.ExecuteAsync(CancellationToken.None);
        await AssertFeedAsync(h, airbnb, deprecated: true);

        await SetMappingStatusAsync(h, OctorateMappingStatus.Rejected);
        await job.ExecuteAsync(CancellationToken.None);
        await AssertFeedAsync(h, airbnb, deprecated: false);
    }

    [Fact]
    public async Task TheBlocksOfAPropertyWithNoConfirmedMapping_AreRemoved_ButNotThoseOfAPropertyWhoseConnectionIsOnlyDisconnected()
    {
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

    [Fact]
    public async Task EachOrgIsReadUnderItsOwnScope_AndWritesItsOwnRowsOnly()
    {
        var h = new Octorate.BookingImportHarness();
        var propertyB = await SeedOrgBListingAsync(h);
        var scope = h.Scope();
        var seen = new Dictionary<string, Guid>();
        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyCollection<string> _, DateOnly from, DateOnly to, CancellationToken _) =>
            {
                seen[Accommodation] = scope.OrgId;
                return Task.FromResult(Window([Closed(10)], from, to));
            });
        h.Client
            .Setup(c => c.GetCalendarAsync("B-ACC", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyCollection<string> _, DateOnly from, DateOnly to, CancellationToken _) =>
            {
                seen["B-ACC"] = scope.OrgId;
                return Task.FromResult(Window([Closed(15, "B-ROOM")], from, to));
            });
        await using var db = h.NewDb();

        await Job(h, db, scope).ExecuteAsync(CancellationToken.None);

        Assert.Equal(OrgA, seen[Accommodation]);
        Assert.Equal(OrgB, seen["B-ACC"]);
        Assert.Throws<InvalidOperationException>(() => scope.OrgId);

        await using var read = h.NewDb();
        var blockA = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == h.PropertyId);
        var blockB = await read.CalendarBlocks.AsNoTracking().SingleAsync(b => b.PropertyId == propertyB);
        Assert.Equal((OrgA, Utc(10)), (blockA.OrgId, blockA.StartUtc));
        Assert.Equal((OrgB, Utc(15)), (blockB.OrgId, blockB.StartUtc));
    }

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

    private static void Serve(Octorate.BookingImportHarness h, params OctorateCalendarDay[] days)
    {
        h.Client.Reset();
        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyCollection<string> _, DateOnly from, DateOnly to, CancellationToken _) => Task.FromResult(Window(days, from, to)));
    }

    private static void ServeAsIs(Octorate.BookingImportHarness h, params OctorateCalendarDay[] days)
    {
        h.Client.Reset();
        h.Client
            .Setup(c => c.GetCalendarAsync(Accommodation, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(days);
    }

    private static void ServeFor(Octorate.BookingImportHarness h, string accommodation, string room, params OctorateCalendarDay[] days) =>
        h.Client
            .Setup(c => c.GetCalendarAsync(accommodation, It.Is<IReadOnlyCollection<string>>(p => p.Single() == room), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyCollection<string> _, DateOnly from, DateOnly to, CancellationToken _) => Task.FromResult(Window(days, from, to)));

    private static OctorateCalendarDay[] Window(OctorateCalendarDay[] days, DateOnly from, DateOnly to)
    {
        var answer = days.ToList();
        foreach (var room in days.Select(d => d.ProductId).Distinct(StringComparer.Ordinal))
        {
            foreach (var end in new[] { from, to })
            {
                if (!days.Any(d => d.ProductId == room && d.Date == end))
                    answer.Add(Bare(end, room));
            }
        }

        return [.. answer];
    }

    private static DateTime Utc(int day) => new(2026, 11, day, 0, 0, 0, DateTimeKind.Utc);

    private static OctorateCalendarDay Closed(int day, string product = Room) =>
        new(product, new DateOnly(2026, 11, day), 0, 100m, false, null, null, null, null);

    private static OctorateCalendarDay Open(int day) =>
        new(Room, new DateOnly(2026, 11, day), 1, 100m, false, 2, null, null, null);

    private static OctorateCalendarDay Stopped(int day) =>
        new(Room, new DateOnly(2026, 11, day), 1, 100m, true, null, null, null, null);

    private static OctorateCalendarDay NeverOpened(int day) =>
        new(Room, new DateOnly(2026, 11, day), 0, 0m, false, null, null, null, null);

    private static OctorateCalendarDay Bare(DateOnly date, string product = Room) =>
        new(product, date, null, null, null, null, null, null, null);

    private static OctorateCalendarDay OpenOn(DateOnly date) =>
        new(Room, date, 1, 100m, false, 2, null, null, null);

    private static async Task AssertUntouchedAsync(Octorate.BookingImportHarness h, Guid feedId)
    {
        await using var read = h.NewDb();
        var block = await read.CalendarBlocks.AsNoTracking().SingleAsync();
        Assert.Equal((Utc(9), Utc(11)), (block.StartUtc, block.EndUtc));
        Assert.False((await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == feedId)).Deprecated);
    }

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

    private static async Task AssertFeedAsync(Octorate.BookingImportHarness h, Guid feedId, bool deprecated)
    {
        await using var read = h.NewDb();
        var feed = await read.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == feedId);
        Assert.Equal(deprecated, feed.Deprecated);
        Assert.Equal(deprecated, feed.DeprecatedAt is not null);
    }

    private static async Task SetConnectionAsync(Octorate.BookingImportHarness h, OctorateConnectionStatus status)
    {
        await using var db = h.NewDb();
        (await db.OctorateConnections.SingleAsync()).Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task SetMappingStatusAsync(Octorate.BookingImportHarness h, OctorateMappingStatus status)
    {
        await using var db = h.NewDb();
        (await db.OctorateListingMappings.SingleAsync(m => m.Id == h.MappingId)).Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task SetChannelsAsync(Octorate.BookingImportHarness h, string channelIdsJson)
    {
        await using var db = h.NewDb();
        (await db.OctorateListingMappings.SingleAsync(m => m.Id == h.MappingId)).ChannelIdsJson = channelIdsJson;
        await db.SaveChangesAsync();
    }

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
