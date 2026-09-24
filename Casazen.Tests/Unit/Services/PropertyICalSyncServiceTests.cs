using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
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

        return new PropertyICalSyncService(
            db,
            externalHttpClient,
            new ICalImportService(),
            new ICalExportService(),
            configuration,
            Mock.Of<ILogger<PropertyICalSyncService>>());
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

    [Fact]
    public async Task SyncPropertyFeedAsync_WhenFeedHasNoUsableEvents_PreservesExistingBlocksAndSetsFailure()
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
            ExternalUid = "existing-block",
            StartUtc = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, ics);
        await service.SyncPropertyFeedAsync(propertyId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        var blocks = await db.CalendarBlocks.Where(b => b.PropertyId == propertyId).ToListAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.Equal(ICalErrorCodes.InvalidFormat, feed.LastError);
        Assert.Single(blocks);
        Assert.Equal("existing-block", blocks[0].ExternalUid);
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

    private static void SeedFeed(AppDbContext db, Guid propertyId, Guid orgId, string importUrl)
    {
        db.PropertyICalFeeds.Add(new PropertyICalFeed
        {
            PropertyId = propertyId,
            OrgId = orgId,
            ImportUrl = importUrl,
            ExportToken = Guid.NewGuid(),
        });
        db.SaveChanges();
    }

    private sealed class FakeExternalHttpClient(string? icsContent, ExternalFetchFailure? failure = null)
        : ISafeExternalHttpClient
    {
        public const string ExceptionMessage = "Connection refused (10.0.0.5:443)";

        public int Downloads { get; private set; }

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (failure is { } f)
                throw new ExternalFetchException(f, ExceptionMessage);

            return Task.FromResult(icsContent ?? string.Empty);
        }
    }
}
