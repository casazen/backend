using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyICalSyncServiceTests
{
    private static PropertyICalSyncService CreateService(AppDbContext db, string? icsContent = null, bool fail = false)
    {
        var handler = new FakeIcalHandler(icsContent, fail);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("IcalSync")).Returns(() => new HttpClient(handler));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:ApiBaseUrl"] = "https://api.test.casazen.app",
            })
            .Build();

        return new PropertyICalSyncService(
            db,
            factory.Object,
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

        var service = CreateService(db, icsContent: null, fail: true);
        await service.SyncPropertyFeedAsync(propertyId);

        var feed = await db.PropertyICalFeeds.FirstAsync();
        Assert.Equal(PropertyICalImportStatus.Failure, feed.LastImportStatus);
        Assert.False(string.IsNullOrWhiteSpace(feed.LastError));
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

    private sealed class FakeIcalHandler : HttpMessageHandler
    {
        private readonly string? _icsContent;
        private readonly bool _fail;

        public FakeIcalHandler(string? icsContent, bool fail = false)
        {
            _icsContent = icsContent;
            _fail = fail;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_fail)
                throw new HttpRequestException("fetch failed");

            var content = _icsContent ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            });
        }
    }
}
