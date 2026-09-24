using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-10 (A2-10, A2-12) on PostgreSQL, where column lengths and the (PropertyId, ExternalUid) unique index are
/// enforced: a failing feed does not stop the others, an empty feed removes the imported blocks, two runs on the same
/// property do not collide. Downloads go through a scripted <see cref="ISafeExternalHttpClient"/>: no network.
/// </summary>
public class PropertyICalSyncPostgresTests : IClassFixture<PropertyICalSyncPostgresTests.Factory>
{
    private readonly Factory _factory;

    public PropertyICalSyncPostgresTests(Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task SyncAllFeedsAsync_FeedsFailingOnSaveOrDownload_DoNotStopTheOtherFeeds()
    {
        var run = Guid.NewGuid().ToString("N");
        // Feeds run least recently synced first: the two failing feeds go before the good ones.
        var clash = await SeedFeedAsync($"https://clash-{run}.example.com/cal.ics", lastImportAt: null);
        var broken = await SeedFeedAsync($"https://broken-{run}.example.com/cal.ics", lastImportAt: DateTime.UtcNow.AddDays(-2));
        var messy = await SeedFeedAsync($"https://messy-{run}.example.com/cal.ics", lastImportAt: DateTime.UtcNow.AddDays(-1));
        var plain = await SeedFeedAsync($"https://plain-{run}.example.com/cal.ics", lastImportAt: DateTime.UtcNow.AddHours(-1));

        // A block of another source already holds the UID of the feed: the insert violates the unique index (23505).
        await SeedBlockAsync(clash, "clash-uid", CalendarBlockSource.Manual);
        _factory.Feeds[clash.Url] = () => Feed(Event("clash-uid", "20261010", "20261012"));
        _factory.Feeds[broken.Url] = () => throw new InvalidOperationException("unexpected client failure");
        // Before PC-10 each of these broke the save: SUMMARY over varchar(500), the same UID twice.
        _factory.Feeds[messy.Url] = () => Feed(
            Event("messy-uid", "20261010", "20261012", summary: new string('s', 2000)),
            Event("messy-uid", "20261010", "20261012", summary: new string('s', 2000)),
            Event("messy-uid", "20261101", "20261103"));
        _factory.Feeds[plain.Url] = () => Feed(Event("plain-uid", "20261010", "20261012"));

        await using (var jobScope = _factory.Services.CreateAsyncScope())
        {
            await jobScope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncAllFeedsAsync();
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clashFeed = await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == clash.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Failure, clashFeed.LastImportStatus);
        Assert.Equal(ICalErrorCodes.SyncFailed, clashFeed.LastError);
        Assert.Equal(CalendarBlockSource.Manual, (await BlocksAsync(db, clash.PropertyId)).Single().Source);

        var brokenFeed = await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == broken.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Failure, brokenFeed.LastImportStatus);
        Assert.Equal(ICalErrorCodes.SyncFailed, brokenFeed.LastError);

        var messyFeed = await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == messy.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Success, messyFeed.LastImportStatus);
        var messyBlocks = await BlocksAsync(db, messy.PropertyId);
        Assert.Equal(["messy-uid#20261010", "messy-uid#20261101"], messyBlocks.Select(b => b.ExternalUid));
        Assert.Equal(CalendarBlock.SummaryMaxLength, messyBlocks[0].Summary!.Length);

        var plainFeed = await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == plain.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Success, plainFeed.LastImportStatus);
        Assert.Equal("plain-uid", (await BlocksAsync(db, plain.PropertyId)).Single().ExternalUid);
    }

    [PostgresFact]
    public async Task SyncPropertyFeedAsync_ValidFeedWithoutEvents_RemovesTheImportedBlocks()
    {
        var feed = await SeedFeedAsync($"https://empty-{Guid.NewGuid():N}.example.com/cal.ics", lastImportAt: null);
        await SeedBlockAsync(feed, "cancelled-on-booking", CalendarBlockSource.ICalImport);
        await SeedBlockAsync(feed, "cancelled-on-airbnb", CalendarBlockSource.ICalImport);
        _factory.Feeds[feed.Url] = () => Feed();

        await using (var jobScope = _factory.Services.CreateAsyncScope())
        {
            await jobScope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncPropertyFeedAsync(feed.PropertyId);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await BlocksAsync(db, feed.PropertyId));
        var stored = await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == feed.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Success, stored.LastImportStatus);
        Assert.Null(stored.LastError);
    }

    // The 15-minute job and the first sync of a new URL can run on the same property at once: the advisory lock makes
    // the second wait and update the blocks the first inserted, instead of inserting the same UIDs again (23505).
    [PostgresFact]
    public async Task SyncPropertyFeedAsync_TwoConcurrentRuns_WriteEachBlockOnceAndSucceed()
    {
        var feed = await SeedFeedAsync($"https://concurrent-{Guid.NewGuid():N}.example.com/cal.ics", lastImportAt: null);
        var events = Enumerable.Range(0, 30)
            .Select(i => Event($"reservation-{i}", $"202611{i % 28 + 1:00}", $"202612{i % 28 + 1:00}"))
            .ToArray();
        var bothDownloading = new Barrier(2);
        _factory.Feeds[feed.Url] = () =>
        {
            // Both runs hold the downloaded feed before either writes.
            bothDownloading.SignalAndWait(TimeSpan.FromSeconds(30));
            return Feed(events);
        };

        try
        {
            await Task.WhenAll(RunAsync(), RunAsync());
        }
        finally
        {
            _factory.Feeds.TryRemove(feed.Url, out _); // later batch runs of the class must not wait on the barrier
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blocks = await BlocksAsync(db, feed.PropertyId);
        Assert.Equal(30, blocks.Count);
        Assert.Equal(30, blocks.Select(b => b.ExternalUid).Distinct().Count());
        var stored = await db.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.PropertyId == feed.PropertyId);
        Assert.Equal(PropertyICalImportStatus.Success, stored.LastImportStatus);
        Assert.Null(stored.LastError);

        async Task RunAsync()
        {
            await Task.Yield();
            await using var runScope = _factory.Services.CreateAsyncScope();
            await runScope.ServiceProvider.GetRequiredService<PropertyICalSyncService>().SyncPropertyFeedAsync(feed.PropertyId);
        }
    }

    private static string Event(string uid, string start, string end, string? summary = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\n"
        + (summary is null ? string.Empty : $"SUMMARY:{summary}\r\n")
        + "END:VEVENT\r\n";

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static Task<List<CalendarBlock>> BlocksAsync(AppDbContext db, Guid propertyId) =>
        db.CalendarBlocks.AsNoTracking().Where(b => b.PropertyId == propertyId).OrderBy(b => b.ExternalUid).ToListAsync();

    private async Task<SeededFeed> SeedFeedAsync(string url, DateTime? lastImportAt)
    {
        var property = await _factory.SeedPropertyAsync($"auth0|ical-{Guid.NewGuid():N}");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PropertyICalFeeds.Add(new PropertyICalFeed
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ImportUrl = url,
            ExportToken = Guid.NewGuid(),
            LastImportAt = lastImportAt,
        });
        await db.SaveChangesAsync();
        return new SeededFeed(property.Id, property.OrgId, url);
    }

    private async Task SeedBlockAsync(SeededFeed feed, string externalUid, CalendarBlockSource source)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = feed.PropertyId,
            OrgId = feed.OrgId,
            Source = source,
            ExternalUid = externalUid,
            StartUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
    }

    private sealed record SeededFeed(Guid PropertyId, Guid OrgId, string Url);

    /// <summary>The integration host with the download client replaced by <see cref="Feeds"/> (URL → body).</summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        public ConcurrentDictionary<string, Func<string>> Feeds { get; } = new(StringComparer.Ordinal);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISafeExternalHttpClient>();
                services.AddSingleton<ISafeExternalHttpClient>(new ScriptedExternalHttpClient(Feeds));
            });
        }
    }

    private sealed class ScriptedExternalHttpClient(ConcurrentDictionary<string, Func<string>> feeds) : ISafeExternalHttpClient
    {
        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        // Unknown URLs (feeds seeded by other tests of the class) answer like an unreachable host.
        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) =>
            feeds.TryGetValue(url, out var body)
                ? Task.Run(body, cancellationToken)
                : throw new ExternalFetchException(ExternalFetchFailure.Unreachable, "not scripted");
    }
}
