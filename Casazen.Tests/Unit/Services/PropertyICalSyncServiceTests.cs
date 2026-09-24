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
    public async Task SyncPropertyFeedAsync_CreatesBlocksFromParsedEvents()
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
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");

        var service = CreateService(db, ics);
        await service.SyncPropertyFeedAsync(propertyId);

        var blocks = await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync();
        Assert.Single(blocks);
        Assert.Equal("block-1", blocks[0].ExternalUid);
        // 10:00Z is noon in Rome: nights of 10 and 11 July, stored as midnight UTC of the dates.
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), blocks[0].StartUtc);
        Assert.Equal(new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc), blocks[0].EndUtc);
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
    }

    [Fact]
    public async Task SyncPropertyFeedAsync_IsIdempotent()
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
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");

        var service = CreateService(db, ics);
        await service.SyncPropertyFeedAsync(propertyId);
        await service.SyncPropertyFeedAsync(propertyId);

        Assert.Equal(1, await db.CalendarBlocks.CountAsync(b => b.PropertyId == propertyId));
    }

    [Fact]
    public async Task SyncPropertyFeedAsync_RemovesOrphanBlocks()
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
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
            Source = CalendarBlockSource.ICalImport,
            ExternalUid = "orphan",
            StartUtc = DateTime.UtcNow,
            EndUtc = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, ics);
        await service.SyncPropertyFeedAsync(propertyId);

        var uids = await db.CalendarBlocks
            .Where(b => b.PropertyId == propertyId)
            .Select(b => b.ExternalUid)
            .ToListAsync();
        Assert.DoesNotContain("orphan", uids);
        Assert.Contains("block-2", uids);
    }

    [Fact]
    public async Task SyncPropertyFeedAsync_OnFetchFailure_SetsFailureStatus()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");

        var service = CreateService(db, icsContent: null, failure: ExternalFetchFailure.Unreachable);
        await service.SyncPropertyFeedAsync(propertyId);

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
    public async Task SyncPropertyFeedAsync_OnFetchFailure_StoresStableCodeNotExceptionMessage(
        ExternalFetchFailure failure,
        string expectedCode)
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");

        var service = CreateService(db, icsContent: null, failure: failure);
        await service.SyncPropertyFeedAsync(propertyId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.Equal(expectedCode, feed.LastError);
        Assert.DoesNotContain(FakeExternalHttpClient.ExceptionMessage, feed.LastError);
    }

    [Fact]
    public async Task SetImportUrlAsync_ValidUrl_SavesUrlAndMarksSyncingWithoutDownloading()
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var client = new FakeExternalHttpClient("BEGIN:VCALENDAR");

        var service = CreateService(db, client);
        var feed = await service.SetImportUrlAsync(propertyId, Guid.NewGuid(), " https://www.airbnb.it/calendar/ical/1.ics?s=abc ");

        Assert.Equal("https://www.airbnb.it/calendar/ical/1.ics?s=abc", feed.ImportUrl);
        Assert.Equal(PropertyICalImportStatus.Syncing, feed.LastImportStatus);
        Assert.Null(feed.LastError);
        Assert.Equal(0, client.Downloads);
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
    public async Task SetImportUrlAsync_NotAnExternalHttpsUrl_ThrowsInvalidUrlAndSavesNothing(string url)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SetImportUrlAsync(Guid.NewGuid(), Guid.NewGuid(), url));

        Assert.Equal(ICalErrorCodes.InvalidUrl, ex.Code);
        Assert.Equal("ICalInvalidUrl", ex.MessageKey);
        Assert.Empty(db.PropertyICalFeeds);
    }

    // PC-10 (A2-10, A9-13): the OTA cancels the only reservation and the feed comes back empty. It is a valid feed:
    // the imported blocks are removed and the status is Success (the old test expected Failure and kept them).
    [Fact]
    public async Task SyncPropertyFeedAsync_ValidFeedWithoutEvents_RemovesImportedBlocksAndSetsSuccess()
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
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics", lastError: ICalErrorCodes.InvalidFormat);
        SeedBlock(db, propertyId, orgId, "cancelled-reservation");
        SeedBlock(db, propertyId, orgId, "manual-block", CalendarBlockSource.Manual);

        var service = CreateService(db, ics);
        await service.SyncPropertyFeedAsync(propertyId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Success, feed.LastImportStatus);
        Assert.Null(feed.LastError);
        Assert.NotNull(feed.LastImportAt);
        var remaining = await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync();
        Assert.Equal("manual-block", Assert.Single(remaining).ExternalUid);
    }

    [Fact]
    public async Task SyncPropertyFeedAsync_FeedWithOnlyCancelledEvents_RemovesTheirBlocks()
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
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        SeedBlock(db, propertyId, orgId, "reservation-1");

        await CreateService(db, ics).SyncPropertyFeedAsync(propertyId);

        Assert.Empty(await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync());
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
    }

    // Only a document that is not a readable iCalendar is an error: the blocks are kept, the code is stable.
    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Accedi per continuare</body></html>")]
    [InlineData("")]
    [InlineData("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:cut\nDTSTART:20261010T100000Z\n")]
    public async Task SyncPropertyFeedAsync_NotAReadableCalendar_KeepsBlocksAndStoresInvalidFormat(string content)
    {
        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        SeedBlock(db, propertyId, orgId, "existing-block");

        await CreateService(db, content).SyncPropertyFeedAsync(propertyId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.Equal(ICalErrorCodes.InvalidFormat, feed.LastError);
        Assert.Equal("existing-block", Assert.Single(await db.CalendarBlocks.ToListAsync()).ExternalUid);
    }

    // An event that cannot be read (end before start) is skipped: it does not fail the feed or the other events.
    [Fact]
    public async Task SyncPropertyFeedAsync_UnreadableEvent_IsSkippedAndTheOthersAreImported()
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
        SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");

        await CreateService(db, ics).SyncPropertyFeedAsync(propertyId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Success, feed.LastImportStatus);
        Assert.Equal("valid-block", Assert.Single(await db.CalendarBlocks.ToListAsync()).ExternalUid);
    }

    // An event still in the feed but unreadable is not proof that its reservation is gone: its block stays, while the
    // blocks of events that left the feed are removed.
    [Fact]
    public async Task SyncPropertyFeedAsync_UnreadableEventOfAnImportedBlock_KeepsThatBlock()
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
        SeedFeed(db, propertyId, orgId, "https://example.com/cal.ics");
        SeedBlock(db, propertyId, orgId, "existing-block");
        SeedBlock(db, propertyId, orgId, "gone-block");

        await CreateService(db, ics).SyncPropertyFeedAsync(propertyId);

        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
        Assert.Equal("existing-block", Assert.Single(await db.CalendarBlocks.ToListAsync()).ExternalUid);
    }

    // A2-12: a 2000-character SUMMARY and a UID repeated in the feed no longer break the save.
    [Fact]
    public async Task SyncPropertyFeedAsync_LongSummaryAndRepeatedUid_StoresFittingUniqueBlocks()
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
        SeedFeed(db, propertyId, Guid.NewGuid(), "https://example.com/cal.ics");

        await CreateService(db, ics).SyncPropertyFeedAsync(propertyId);

        var blocks = await db.CalendarBlocks.OrderBy(b => b.StartUtc).ToListAsync();
        Assert.Equal(["repeated#20261010", "repeated#20261101"], blocks.Select(b => b.ExternalUid));
        Assert.All(blocks, b => Assert.True((b.Summary?.Length ?? 0) <= CalendarBlock.SummaryMaxLength));
        Assert.Equal(PropertyICalImportStatus.Success, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
    }

    // A new URL saved while the old one was downloading: the old result is dropped, the new URL's job syncs.
    [Fact]
    public async Task SyncPropertyFeedAsync_UrlReplacedDuringTheDownload_WritesNothing()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:old-feed-block
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            END:VEVENT
            END:VCALENDAR
            """;

        await using var db = CreateDb();
        var propertyId = Guid.NewGuid();
        SeedFeed(db, propertyId, Guid.NewGuid(), "https://old.example.com/cal.ics");
        var client = new FakeExternalHttpClient(ics)
        {
            OnDownload = () =>
            {
                var feed = db.PropertyICalFeeds.Single();
                feed.ImportUrl = "https://new.example.com/cal.ics";
                feed.LastImportStatus = PropertyICalImportStatus.Syncing;
                db.SaveChanges();
                db.ChangeTracker.Clear();
            },
        };

        await CreateService(db, client).SyncPropertyFeedAsync(propertyId);

        Assert.Empty(await db.CalendarBlocks.ToListAsync());
        Assert.Equal(PropertyICalImportStatus.Syncing, (await db.PropertyICalFeeds.FirstAsync()).LastImportStatus);
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

    private static void SeedFeed(
        AppDbContext db,
        Guid propertyId,
        Guid orgId,
        string importUrl,
        string? lastError = null,
        DateTime? lastImportAt = null)
    {
        db.PropertyICalFeeds.Add(new PropertyICalFeed
        {
            PropertyId = propertyId,
            OrgId = orgId,
            ImportUrl = importUrl,
            ExportToken = Guid.NewGuid(),
            LastError = lastError,
            LastImportAt = lastImportAt,
            LastImportStatus = lastError is null ? null : PropertyICalImportStatus.Failure,
        });
        db.SaveChanges();
    }

    private static void SeedBlock(
        AppDbContext db,
        Guid propertyId,
        Guid orgId,
        string externalUid,
        CalendarBlockSource source = CalendarBlockSource.ICalImport)
    {
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
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
