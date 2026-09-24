using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyICalSyncServiceTests
{
    private static PropertyICalSyncService CreateService(
        AppDbContext db,
        string? icsContent = null,
        ExternalFetchFailure? failure = null) =>
        CreateService(db, new FakeExternalHttpClient(icsContent, failure));

    private static PropertyICalSyncService CreateService(AppDbContext db, ISafeExternalHttpClient externalHttpClient)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:ApiBaseUrl"] = "https://api.test.casazen.app",
            })
            .Build();

        return ICalTestServices.PropertySync(db, externalHttpClient, configuration);
    }

    [Fact]
    public async Task SyncFeedAsync_CreatesBlocksFromParsedEvents()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:block-1
            DTSTART:20260710T100000Z
            DTEND:20260712T100000Z
            SUMMARY:Reserved
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");

        var service = CreateService(db, ics);
        await service.SyncFeedAsync(feedId);

        var blocks = await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync();
        Assert.Single(blocks);
        Assert.Equal("block-1", blocks[0].ExternalUid);
        // 10:00Z is noon in Rome: nights of 10 and 11 July, stored as midnight UTC of the dates.
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), blocks[0].StartUtc);
        Assert.Equal(new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc), blocks[0].EndUtc);
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
    }

    [Fact]
    public async Task SyncFeedAsync_IsIdempotent()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:block-1
            DTSTART:20260710T100000Z
            DTEND:20260712T100000Z
            SUMMARY:Reserved
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");

        var service = CreateService(db, ics);
        await service.SyncFeedAsync(feedId);
        await service.SyncFeedAsync(feedId);

        Assert.Equal(1, await db.CalendarBlocks.CountAsync(b => b.PropertyId == propertyId));
    }

    [Fact]
    public async Task SyncFeedAsync_RemovesOrphanBlocks()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:block-2
            DTSTART:20260710T100000Z
            DTEND:20260712T100000Z
            SUMMARY:Reserved
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
            FeedId = feedId,
            Source = CalendarBlockSource.ICalImport,
            ExternalUid = "orphan",
            StartUtc = DateTime.UtcNow,
            EndUtc = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, ics);
        await service.SyncFeedAsync(feedId);

        var uids = await db.CalendarBlocks
            .Where(b => b.PropertyId == propertyId)
            .Select(b => b.ExternalUid)
            .ToListAsync();
        Assert.DoesNotContain("orphan", uids);
        Assert.Contains("block-2", uids);
    }

    [Fact]
    public async Task SyncFeedAsync_OnFetchFailure_SetsFailureStatus()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");

        var service = CreateService(db, icsContent: null, failure: ExternalFetchFailure.Unreachable);
        await service.SyncFeedAsync(feedId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.Equal(ICalErrorCodes.Unreachable, feed.LastError);
    }

    [Theory]
    [InlineData(ExternalFetchFailure.BlockedDestination, ICalErrorCodes.Unreachable)]
    [InlineData(ExternalFetchFailure.RedirectRejected, ICalErrorCodes.Unreachable)]
    [InlineData(ExternalFetchFailure.Timeout, ICalErrorCodes.Unreachable)]
    [InlineData(ExternalFetchFailure.TooLarge, ICalErrorCodes.TooLarge)]
    [InlineData(ExternalFetchFailure.InvalidUrl, ICalErrorCodes.InvalidUrl)]
    public async Task SyncFeedAsync_OnFetchFailure_StoresStableCodeNotExceptionMessage(
        ExternalFetchFailure failure,
        string expectedCode)
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");

        var service = CreateService(db, icsContent: null, failure: failure);
        await service.SyncFeedAsync(feedId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.Equal(expectedCode, feed.LastError);
        Assert.DoesNotContain(FakeExternalHttpClient.ExceptionMessage, feed.LastError);
    }

    // PC-11: a feed is added without downloading it (FD-16: the first sync is a background job).
    [Fact]
    public async Task AddFeedAsync_ValidUrl_SavesTrimmedFeedMarkedSyncingWithoutDownloading()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var client = new FakeExternalHttpClient("BEGIN:VCALENDAR");

        var feed = await CreateService(db, client).AddFeedAsync(
            propertyId, Guid.NewGuid(), channel: null, label: "  Camera 2  ", " https://www.airbnb.it/calendar/ical/1.ics?s=abc ");

        Assert.Equal("https://www.airbnb.it/calendar/ical/1.ics?s=abc", feed.ImportUrl);
        Assert.Equal(ICalFeedChannel.Airbnb, feed.Channel);
        Assert.Equal("Camera 2", feed.Label);
        Assert.Equal(PropertyICalImportStatus.Syncing, feed.LastImportStatus);
        Assert.Null(feed.LastError);
        Assert.Equal(0, client.Downloads);
        Assert.Equal(feed.Id, (await db.PropertyICalFeeds.SingleAsync()).Id);
    }

    // PC-11 (A2-11): the host links Airbnb and Booking.com to the same property.
    [Fact]
    public async Task AddFeedAsync_SecondChannelOnTheSameProperty_KeepsBothFeeds()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var service = CreateService(db);

        await service.AddFeedAsync(propertyId, Guid.NewGuid(), null, null, "https://www.airbnb.it/calendar/ical/1.ics?s=abc");
        await service.AddFeedAsync(propertyId, Guid.NewGuid(), null, null, "https://admin.booking.com/hotel/hoteladmin/ical.html?t=xyz");

        var channels = await db.PropertyICalFeeds.Where(f => f.PropertyId == propertyId).Select(f => f.Channel).ToListAsync();
        Assert.Equal([ICalFeedChannel.Airbnb, ICalFeedChannel.BookingCom], channels.Order());
    }

    [Theory]
    [InlineData("http://example.com/cal.ics")]
    [InlineData("https://127.0.0.1/cal.ics")]
    [InlineData("https://10.1.2.3/cal.ics")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("https://localhost/cal.ics")]
    [InlineData("https://example.com:8443/cal.ics")]
    [InlineData("")]
    public async Task AddFeedAsync_NotAnExternalHttpsUrl_ThrowsInvalidUrlAndSavesNothing(string url)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.AddFeedAsync(Guid.NewGuid(), Guid.NewGuid(), ICalFeedChannel.Other, null, url));

        Assert.Equal(ICalErrorCodes.InvalidUrl, ex.Code);
        Assert.Equal("ICalInvalidUrl", ex.MessageKey);
        Assert.Empty(db.PropertyICalFeeds);
    }

    // Longer URLs would not fit the encrypted column (varchar 4096).
    [Fact]
    public async Task AddFeedAsync_UrlLongerThanTheLimit_ThrowsInvalidUrl()
    {
        await using var db = CreateDb();
        var url = "https://example.com/" + new string('a', PropertyICalFeed.ImportUrlMaxLength);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService(db).AddFeedAsync(Guid.NewGuid(), Guid.NewGuid(), null, null, url));

        Assert.Equal(ICalErrorCodes.InvalidUrl, ex.Code);
        Assert.Empty(db.PropertyICalFeeds);
    }

    [Fact]
    public async Task AddFeedAsync_SameUrlTwice_ThrowsDuplicate()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var service = CreateService(db);
        await service.AddFeedAsync(propertyId, Guid.NewGuid(), null, null, "https://www.airbnb.it/calendar/ical/1.ics?s=abc");

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.AddFeedAsync(propertyId, Guid.NewGuid(), null, "copy", "https://WWW.AIRBNB.IT/calendar/ical/1.ics?s=abc"));

        Assert.Equal(ICalFeedErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.PropertyICalFeeds);
    }

    [Fact]
    public async Task AddFeedAsync_LimitReached_ThrowsLimitWithTheMaximum()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var service = ICalTestServices.PropertySync(
            db, new FakeExternalHttpClient(null), new ConfigurationBuilder().Build(),
            importOptions: new ICalImportOptions { MaxFeedsPerProperty = 2 });
        await service.AddFeedAsync(propertyId, Guid.NewGuid(), null, null, "https://a.example.com/1.ics");
        await service.AddFeedAsync(propertyId, Guid.NewGuid(), null, null, "https://b.example.com/2.ics");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.AddFeedAsync(propertyId, Guid.NewGuid(), null, null, "https://c.example.com/3.ics"));

        Assert.Equal(ICalFeedErrorCodes.LimitReached, ex.Code);
        Assert.Equal(2, Assert.Single(ex.MessageArgs));
        Assert.Equal(2, await db.PropertyICalFeeds.CountAsync());
    }

    [Theory]
    [InlineData("Airbnb\nBooking")]
    [InlineData("tab\there")]
    public async Task AddFeedAsync_LabelWithControlCharacters_ThrowsInvalidLabel(string label)
    {
        await using var db = CreateDb();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => CreateService(db).AddFeedAsync(Guid.NewGuid(), Guid.NewGuid(), null, label, "https://example.com/cal.ics"));

        Assert.Equal(ICalFeedErrorCodes.InvalidLabel, ex.Code);
        Assert.Empty(db.PropertyICalFeeds);
    }

    [Theory]
    [InlineData("https://www.airbnb.it/calendar/ical/1.ics?s=a", ICalFeedChannel.Airbnb)]
    [InlineData("https://airbnb.com/calendar/ical/1.ics", ICalFeedChannel.Airbnb)]
    [InlineData("https://www.airbnb.co.uk/calendar/ical/1.ics", ICalFeedChannel.Airbnb)]
    [InlineData("https://admin.booking.com/hotel/hoteladmin/ical.html?t=x", ICalFeedChannel.BookingCom)]
    [InlineData("https://ical.booking.com/v1/export?t=x", ICalFeedChannel.BookingCom)]
    [InlineData("https://notairbnb.example.com/cal.ics", ICalFeedChannel.Other)]
    [InlineData("https://booking.com.example.org/cal.ics", ICalFeedChannel.Other)]
    [InlineData("https://calendar.google.com/calendar/ical/x/basic.ics", ICalFeedChannel.Other)]
    public void InferChannel_KnownOtaHosts_MapToTheirChannel(string url, ICalFeedChannel expected) =>
        Assert.Equal(expected, PropertyICalSyncService.InferChannel(new Uri(url)));

    // PC-11: removing a feed deletes its blocks and only those: the other feed and the manual block stay.
    [Fact]
    public async Task RemoveFeedAsync_TwoFeeds_DeletesOnlyTheBlocksOfTheRemovedFeed()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var airbnb = SeedFeed(db, propertyId, orgId, "https://www.airbnb.it/calendar/ical/1.ics", channel: ICalFeedChannel.Airbnb);
        var booking = SeedFeed(db, propertyId, orgId, "https://admin.booking.com/ical.html?t=1", channel: ICalFeedChannel.BookingCom);
        SeedBlock(db, propertyId, orgId, "airbnb-1", feedId: airbnb);
        SeedBlock(db, propertyId, orgId, "booking-1", feedId: booking);
        SeedBlock(db, propertyId, orgId, "owner-stay", CalendarBlockSource.Manual);

        var removed = await CreateService(db).RemoveFeedAsync(propertyId, airbnb);

        Assert.Equal(1, removed);
        Assert.Equal(booking, (await db.PropertyICalFeeds.SingleAsync()).Id);
        Assert.Equal(["booking-1", "owner-stay"], await db.CalendarBlocks.OrderBy(b => b.ExternalUid).Select(b => b.ExternalUid).ToListAsync());
    }

    [Fact]
    public async Task RemoveFeedAsync_FeedOfAnotherProperty_ThrowsFeedNotFoundAndKeepsIt()
    {
        await using var db = CreateDb();
        var feedId = SeedFeed(db, Guid.NewGuid(), Guid.NewGuid(), "https://example.com/cal.ics");

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).RemoveFeedAsync(Guid.NewGuid(), feedId));

        Assert.Equal(ICalFeedErrorCodes.NotFound, ex.Code);
        Assert.Single(db.PropertyICalFeeds);
    }

    // "Sync now" twice: the second click queues nothing while the first sync is pending.
    [Fact]
    public async Task RequestSyncAsync_FeedAlreadySyncing_DoesNotQueueAgain()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics", lastError: ICalErrorCodes.Unreachable);
        var service = CreateService(db);

        var first = await service.RequestSyncAsync(propertyId, feedId);
        var second = await service.RequestSyncAsync(propertyId, feedId);

        Assert.True(first.Queue);
        Assert.False(second.Queue);
        Assert.Equal(PropertyICalImportStatus.Syncing, (await db.PropertyICalFeeds.SingleAsync()).LastImportStatus);
    }

    // PC-11 (A2-11): an empty Booking.com feed frees its own dates, never those imported from Airbnb.
    [Fact]
    public async Task SyncFeedAsync_TwoFeedsOfTheSameProperty_ReplacesOnlyItsOwnBlocks()
    {
        const string emptyFeed = """
            BEGIN:VCALENDAR
            VERSION:2.0
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var airbnb = SeedFeed(db, propertyId, orgId, "https://www.airbnb.it/calendar/ical/1.ics", channel: ICalFeedChannel.Airbnb);
        var booking = SeedFeed(db, propertyId, orgId, "https://admin.booking.com/ical.html?t=1", channel: ICalFeedChannel.BookingCom);
        SeedBlock(db, propertyId, orgId, "same-uid", feedId: airbnb);
        SeedBlock(db, propertyId, orgId, "same-uid", feedId: booking);

        await CreateService(db, emptyFeed).SyncFeedAsync(booking);

        var remaining = await db.CalendarBlocks.SingleAsync();
        Assert.Equal(airbnb, remaining.FeedId);
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.SingleAsync(f => f.Id == booking)).LastImportStatus);
        Assert.Null((await db.PropertyICalFeeds.SingleAsync(f => f.Id == airbnb)).LastImportStatus);
    }

    // A job queued before PC-11 carries a property id: it finds no feed and does nothing.
    [Fact]
    public async Task SyncFeedAsync_UnknownId_DoesNothing()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");
        var client = new FakeExternalHttpClient("BEGIN:VCALENDAR");

        await CreateService(db, client).SyncFeedAsync(propertyId);

        Assert.Equal(0, client.Downloads);
        Assert.Null((await db.PropertyICalFeeds.SingleAsync()).LastImportStatus);
    }

    // PC-10 (A2-10, A9-13): the OTA cancels the only reservation and the feed comes back empty. It is a valid feed:
    // the imported blocks are removed and the status is Success (the old test expected Failure and kept them).
    [Fact]
    public async Task SyncFeedAsync_ValidFeedWithoutEvents_RemovesImportedBlocksAndSetsSuccess()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Booking.com//Booking.com//EN
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics", lastError: ICalErrorCodes.InvalidFormat);
        SeedBlock(db, propertyId, orgId, "cancelled-reservation");
        SeedBlock(db, propertyId, orgId, "manual-block", CalendarBlockSource.Manual);

        var service = CreateService(db, ics);
        await service.SyncFeedAsync(feedId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Success, feed.LastImportStatus);
        Assert.Null(feed.LastError);
        Assert.NotNull(feed.LastImportAt);
        var remaining = await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync();
        Assert.Equal("manual-block", Assert.Single(remaining).ExternalUid);
    }

    [Fact]
    public async Task SyncFeedAsync_FeedWithOnlyCancelledEvents_RemovesTheirBlocks()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:reservation-1
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            STATUS:CANCELLED
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        SeedBlock(db, propertyId, orgId, "reservation-1");

        await CreateService(db, ics).SyncFeedAsync(feedId);

        Assert.Empty(await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync());
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
    }

    // Only a document that is not a readable iCalendar is an error: the blocks are kept, the code is stable.
    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Accedi per continuare</body></html>")]
    [InlineData("")]
    [InlineData("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:cut\nDTSTART:20261010T100000Z\n")]
    public async Task SyncFeedAsync_NotAReadableCalendar_KeepsBlocksAndStoresInvalidFormat(string content)
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        SeedBlock(db, propertyId, orgId, "existing-block");

        await CreateService(db, content).SyncFeedAsync(feedId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.Equal(ICalErrorCodes.InvalidFormat, feed.LastError);
        Assert.Equal("existing-block", Assert.Single(await db.CalendarBlocks.ToListAsync()).ExternalUid);
    }

    // An event that cannot be read (end before start) is skipped: it does not fail the feed or the other events.
    [Fact]
    public async Task SyncFeedAsync_UnreadableEvent_IsSkippedAndTheOthersAreImported()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:invalid-block
            DTSTART:20260712T100000Z
            DTEND:20260710T100000Z
            SUMMARY:Invalid reserved block
            END:VEVENT
            BEGIN:VEVENT
            UID:valid-block
            DTSTART;VALUE=DATE:20260720
            DTEND;VALUE=DATE:20260722
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");

        await CreateService(db, ics).SyncFeedAsync(feedId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Success, feed.LastImportStatus);
        Assert.Equal("valid-block", Assert.Single(await db.CalendarBlocks.ToListAsync()).ExternalUid);
    }

    // An event still in the feed but unreadable is not proof that its reservation is gone: its block stays, while the
    // blocks of events that left the feed are removed.
    [Fact]
    public async Task SyncFeedAsync_UnreadableEventOfAnImportedBlock_KeepsThatBlock()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:existing-block
            DTSTART:20261012T100000Z
            DTEND:20261010T100000Z
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        SeedBlock(db, propertyId, orgId, "existing-block");
        SeedBlock(db, propertyId, orgId, "gone-block");

        await CreateService(db, ics).SyncFeedAsync(feedId);

        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
        Assert.Equal("existing-block", Assert.Single(await db.CalendarBlocks.ToListAsync()).ExternalUid);
    }

    // A2-12: a 2000-character SUMMARY and a UID repeated in the feed no longer break the save.
    [Fact]
    public async Task SyncFeedAsync_LongSummaryAndRepeatedUid_StoresFittingUniqueBlocks()
    {
        var ics = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:repeated
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            SUMMARY:{new string('d', 2000)}
            END:VEVENT
            BEGIN:VEVENT
            UID:repeated
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            SUMMARY:{new string('d', 2000)}
            END:VEVENT
            BEGIN:VEVENT
            UID:repeated
            DTSTART;VALUE=DATE:20261101
            DTEND;VALUE=DATE:20261103
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");

        await CreateService(db, ics).SyncFeedAsync(feedId);

        var blocks = await db.CalendarBlocks.OrderBy(b => b.StartUtc).ToListAsync();
        Assert.Equal(["repeated#20261010", "repeated#20261101"], blocks.Select(b => b.ExternalUid));
        Assert.All(blocks, b => Assert.True((b.Summary?.Length ?? 0) <= CalendarBlock.SummaryMaxLength));
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
    }

    // The feed is removed while it downloads: its result is dropped, no block comes back.
    [Fact]
    public async Task SyncFeedAsync_FeedRemovedDuringTheDownload_WritesNothing()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:removed-feed-block
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var feedId = SeedFeed(db, propertyId, Guid.NewGuid(), "https://old.example.com/cal.ics");
        var client = new FakeExternalHttpClient(ics)
        {
            OnDownload = () =>
            {
                db.PropertyICalFeeds.Remove(db.PropertyICalFeeds.Single());
                db.SaveChanges();
                db.ChangeTracker.Clear();
            },
        };

        await CreateService(db, client).SyncFeedAsync(feedId);

        Assert.Empty(await db.CalendarBlocks.ToListAsync());
        Assert.Empty(await db.PropertyICalFeeds.ToListAsync());
    }

    // A2-12: every feed has its own scope, try/catch and error state; an unexpected failure is stored on that feed and
    // the batch goes on.
    [Fact]
    public async Task SyncAllFeedsAsync_OneFeedThrowsUnexpectedly_StoresItsFailureAndSyncsTheOthers()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:ok-block
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            END:VEVENT
            END:VCALENDAR
            """;
        var databaseName = Guid.NewGuid().ToString();
        var client = new RoutingExternalHttpClient(new Dictionary<string, Func<string>>
        {
            ["https://broken.example.com/cal.ics"] = () => throw new InvalidOperationException("unexpected"),
            ["https://ok-1.example.com/cal.ics"] = () => ics,
            ["https://ok-2.example.com/cal.ics"] = () => ics,
        });
        await using var provider = BuildProvider(databaseName, client);

        var broken = Guid.NewGuid();
        var ok1 = Guid.NewGuid();
        var ok2 = Guid.NewGuid();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Never synced first: the broken feed runs before the others.
            SeedFeed(db, broken, Guid.NewGuid(), "https://broken.example.com/cal.ics");
            SeedFeed(db, ok1, Guid.NewGuid(), "https://ok-1.example.com/cal.ics", lastImportAt: DateTime.UtcNow.AddHours(-1));
            SeedFeed(db, ok2, Guid.NewGuid(), "https://ok-2.example.com/cal.ics", lastImportAt: DateTime.UtcNow.AddMinutes(-30));
        }

        await using (var jobScope = provider.CreateAsyncScope())
        {
            await jobScope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncAllFeedsAsync();
        }

        Assert.Equal(["https://broken.example.com/cal.ics", "https://ok-1.example.com/cal.ics", "https://ok-2.example.com/cal.ics"], client.Requests);
        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feeds = await assertDb.PropertyICalFeeds.ToDictionaryAsync(f => f.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Failure, feeds[broken].LastImportStatus);
        Assert.Equal(ICalErrorCodes.SyncFailed, feeds[broken].LastError);
        Assert.Equal(PropertyICalImportStatus.Success, feeds[ok1].LastImportStatus);
        Assert.Equal(PropertyICalImportStatus.Success, feeds[ok2].LastImportStatus);
        Assert.Equal(2, await assertDb.CalendarBlocks.CountAsync());
    }

    [Fact]
    public async Task HasOverlappingBlockAsync_ReturnsTrueWhenBlockOverlaps()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
            Source = CalendarBlockSource.ICalImport,
            ExternalUid = "b1",
            StartUtc = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var overlaps = await service.HasOverlappingBlockAsync(
            propertyId,
            new DateTime(2026, 7, 12),
            new DateTime(2026, 7, 14));

        Assert.True(overlaps);
    }

    private static AppDbContext CreateDb()
    {
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    private static Guid SeedFeed(
        AppDbContext db,
        Guid propertyId,
        Guid orgId,
        string importUrl,
        string? lastError = null,
        DateTime? lastImportAt = null,
        ICalFeedChannel channel = ICalFeedChannel.Other)
    {
        var feed = new PropertyICalFeed
        {
            PropertyId = propertyId,
            OrgId = orgId,
            Channel = channel,
            ImportUrl = importUrl,
            LastError = lastError,
            LastImportAt = lastImportAt,
            LastImportStatus = lastError is null ? null : PropertyICalImportStatus.Failure,
        };
        db.PropertyICalFeeds.Add(feed);
        db.SaveChanges();
        return feed.Id;
    }

    // An imported block belongs to a feed: feedId, or the first feed of the property.
    private static void SeedBlock(
        AppDbContext db,
        Guid propertyId,
        Guid orgId,
        string externalUid,
        CalendarBlockSource source = CalendarBlockSource.ICalImport,
        Guid? feedId = null)
    {
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
            FeedId = source == CalendarBlockSource.ICalImport
                ? feedId ?? db.PropertyICalFeeds.Where(f => f.PropertyId == propertyId).Select(f => (Guid?)f.Id).First()
                : null,
            Source = source,
            ExternalUid = externalUid,
            StartUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();
    }

    // The job's wiring: each feed resolves its own PropertyICalSyncService (and AppDbContext) from a new scope.
    private static ServiceProvider BuildProvider(string databaseName, ISafeExternalHttpClient externalHttpClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddSingleton(externalHttpClient);
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<ICalImportOptions>();
        services.AddScoped<ICalImportService>();
        services.AddScoped<ICalExportService>();
        services.AddScoped<PropertyICalSyncService>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class FakeExternalHttpClient(string? icsContent, ExternalFetchFailure? failure = null)
        : ISafeExternalHttpClient
    {
        public const string ExceptionMessage = "Connection refused (10.0.0.5:443)";

        public int Downloads { get; private set; }

        public Action? OnDownload { get; init; }

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            Downloads++;
            OnDownload?.Invoke();
            if (failure is { } f)
                throw new ExternalFetchException(f, ExceptionMessage);

            return Task.FromResult(icsContent ?? string.Empty);
        }
    }

    private sealed class RoutingExternalHttpClient(IReadOnlyDictionary<string, Func<string>> routes) : ISafeExternalHttpClient
    {
        public List<string> Requests { get; } = [];

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            Requests.Add(url);
            return Task.FromResult(routes[url]());
        }
    }
}
