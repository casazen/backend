using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-11 (A2-11, A2-20): applies every migration up to the one before <c>AddICalMultiFeed</c>, stores the single feed
/// of each property the way the code before PC-11 did (URL in clear, export token on the feed, blocks without feed),
/// then applies <c>AddICalMultiFeed</c> and the startup encryption of the URLs.
/// </summary>
public class ICalMultiFeedMigrationPostgresTests : IAsyncLifetime
{
    private const string AirbnbUrl = "https://www.airbnb.it/calendar/ical/4815162342.ics?s=0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string BookingUrl = "https://admin.booking.com/hotel/hoteladmin/ical.html?t=5e6f7a8b-9c0d";

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddICalMultiFeed_SingleFeedPerProperty_BecomesOneFeedWithItsBlocksAndTheSameExportToken()
    {
        await using (var db = _database!.CreateContext())
        {
            var migrator = db.GetService<IMigrator>();
            migrator.Migrate(PreviousMigration(db));
            await InsertPrePc11StateAsync(db);
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Export links moved unchanged, also the one of the property that had no import URL.
        var exports = await context.PropertyICalExports.AsNoTracking().ToDictionaryAsync(e => e.PropertyId, e => e.ExportToken);
        Assert.Equal(Seed.ExportA, exports[Seed.PropertyA]);
        Assert.Equal(Seed.ExportB, exports[Seed.PropertyB]);
        Assert.Equal(Seed.ExportC, exports[Seed.PropertyC]);

        // One feed per property that had a URL, same id, channel taken from the host; the export-only row is gone.
        var feeds = await context.PropertyICalFeeds.AsNoTracking().OrderBy(f => f.PropertyId).ToListAsync();
        Assert.Equal([Seed.FeedA, Seed.FeedC], feeds.Select(f => f.Id).Order());
        Assert.Equal(ICalFeedChannel.Airbnb, feeds.Single(f => f.Id == Seed.FeedA).Channel);
        Assert.Equal(ICalFeedChannel.BookingCom, feeds.Single(f => f.Id == Seed.FeedC).Channel);
        Assert.All(feeds, f => Assert.NotEqual(default, f.CreatedAt));

        // No block lost: the imported ones belong to the feed, the manual one to none.
        var blocks = await context.CalendarBlocks.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.FeedId);
        Assert.Equal(4, blocks.Count);
        Assert.Equal(Seed.FeedA, blocks[Seed.BlockA1]);
        Assert.Equal(Seed.FeedA, blocks[Seed.BlockA2]);
        Assert.Null(blocks[Seed.ManualBlockA]);
        Assert.Equal(Seed.FeedC, blocks[Seed.BlockC1]);

        // Until the startup step runs, a URL in clear is still readable (it is not a protected payload)...
        Assert.Equal(AirbnbUrl, (await context.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == Seed.FeedA)).ImportUrl);

        // ... and the startup step encrypts every one of them, once.
        Assert.Equal(2, await PropertyICalFeedUrlEncryption.EncryptLegacyPlaintextUrlsAsync(context, NullLogger.Instance));
        Assert.Equal(0, await PropertyICalFeedUrlEncryption.EncryptLegacyPlaintextUrlsAsync(context, NullLogger.Instance));

        var rawA = await RawImportUrlAsync(context, Seed.FeedA);
        var rawC = await RawImportUrlAsync(context, Seed.FeedC);
        Assert.StartsWith("CfDJ8", rawA);
        Assert.StartsWith("CfDJ8", rawC);
        Assert.DoesNotContain("0a1b2c3d4e5f", rawA);
        context.ChangeTracker.Clear();
        Assert.Equal(AirbnbUrl, (await context.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == Seed.FeedA)).ImportUrl);
        Assert.Equal(BookingUrl, (await context.PropertyICalFeeds.AsNoTracking().SingleAsync(f => f.Id == Seed.FeedC)).ImportUrl);
    }

    // The application's wiring of the context: DI with the Data Protection provider of the test hosts (see
    // CasazenWebApplicationFactory.SharedDataProtectionProvider), so the encrypted column has its converter.
    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CasazenWebApplicationFactory.SharedDataProtectionProvider);
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(
            _database!.ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure")));
        return services.BuildServiceProvider();
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddICalMultiFeed", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    // Test-only SQL with constant table names and GUIDs generated in this class, never input.
    private static async Task InsertPrePc11StateAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({Seed.Org}, 'Org PC-11', {"pc11-" + Seed.Org.ToString("N")}, 0, 'Org PC-11', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({Seed.PropertyA}, 'auth0|pc11', {Seed.Org}, 'A', 'Airbnb', 'Via A 1', 'Roma', '00100', 0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now()),
              ({Seed.PropertyB}, 'auth0|pc11', {Seed.Org}, 'B', 'Export only', 'Via B 2', 'Roma', '00100', 0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now()),
              ({Seed.PropertyC}, 'auth0|pc11', {Seed.Org}, 'C', 'Booking', 'Via C 3', 'Roma', '00100', 0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "PropertyICalFeeds" ("Id", "PropertyId", "OrgId", "ImportUrl", "ExportToken", "LastImportAt", "LastImportStatus")
            VALUES
              ({Seed.FeedA}, {Seed.PropertyA}, {Seed.Org}, {AirbnbUrl}, {Seed.ExportA}, now(), 0),
              ({Seed.FeedB}, {Seed.PropertyB}, {Seed.Org}, NULL, {Seed.ExportB}, NULL, NULL),
              ({Seed.FeedC}, {Seed.PropertyC}, {Seed.Org}, {BookingUrl}, {Seed.ExportC}, NULL, 2);

            INSERT INTO "CalendarBlocks" ("Id", "PropertyId", "OrgId", "Source", "ExternalUid", "StartUtc", "EndUtc", "Summary")
            VALUES
              ({Seed.BlockA1}, {Seed.PropertyA}, {Seed.Org}, 0, 'airbnb-1', '2026-10-10', '2026-10-12', 'Reserved'),
              ({Seed.BlockA2}, {Seed.PropertyA}, {Seed.Org}, 0, 'airbnb-2', '2026-11-01', '2026-11-03', 'Reserved'),
              ({Seed.ManualBlockA}, {Seed.PropertyA}, {Seed.Org}, 1, NULL, '2026-12-01', '2026-12-02', NULL),
              ({Seed.BlockC1}, {Seed.PropertyC}, {Seed.Org}, 0, 'airbnb-1', '2026-10-10', '2026-10-12', 'Closed');
            """);
    }

    private static Task<string> RawImportUrlAsync(AppDbContext db, Guid feedId) =>
        db.Database
            .SqlQuery<string>($"SELECT \"ImportUrl\" AS \"Value\" FROM \"PropertyICalFeeds\" WHERE \"Id\" = {feedId}")
            .SingleAsync();

    private static class Seed
    {
        public static readonly Guid Org = Guid.NewGuid();
        public static readonly Guid PropertyA = Guid.NewGuid();
        public static readonly Guid PropertyB = Guid.NewGuid();
        public static readonly Guid PropertyC = Guid.NewGuid();
        public static readonly Guid FeedA = Guid.NewGuid();
        public static readonly Guid FeedB = Guid.NewGuid();
        public static readonly Guid FeedC = Guid.NewGuid();
        public static readonly Guid ExportA = Guid.NewGuid();
        public static readonly Guid ExportB = Guid.NewGuid();
        public static readonly Guid ExportC = Guid.NewGuid();
        public static readonly Guid BlockA1 = Guid.NewGuid();
        public static readonly Guid BlockA2 = Guid.NewGuid();
        public static readonly Guid ManualBlockA = Guid.NewGuid();
        public static readonly Guid BlockC1 = Guid.NewGuid();
    }
}
