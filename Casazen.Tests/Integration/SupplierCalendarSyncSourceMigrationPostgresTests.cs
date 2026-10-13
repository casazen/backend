using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Migrations;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-15 migration (<see cref="AddSupplierCalendarSyncSource"/>) on real PostgreSQL. The days written before it cannot
/// be told apart (feed or supplier): they all become manual, so no sync ever frees a day the supplier may have closed
/// (prudent choice, runbook ical.md). The sync state is filled from the stored error and last sync. The rows are
/// inserted with raw SQL on the schema right before the migration.
/// </summary>
public class SupplierCalendarSyncSourceMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_ExistingDaysAndProfiles_DaysBecomeManualAndSyncStateIsFilledFromStoredData()
    {
        var failed = Guid.NewGuid();
        var synced = Guid.NewGuid();
        var neverSynced = Guid.NewGuid();
        var noFeed = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await InsertProfileAsync(db, failed, "https://feeds.example.com/failed.ics", lastSyncAt: DateTime.UtcNow, error: "ical_unreachable");
            await InsertProfileAsync(db, synced, "https://feeds.example.com/synced.ics", lastSyncAt: DateTime.UtcNow, error: null);
            await InsertProfileAsync(db, neverSynced, "https://feeds.example.com/new.ics", lastSyncAt: null, error: null);
            await InsertProfileAsync(db, noFeed, feedUrl: null, lastSyncAt: null, error: null);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SupplierAvailability" ("Id", "OrgId", "Date", "Available")
                VALUES ({Guid.NewGuid()}, {synced}, DATE '2026-10-10', false),
                       ({Guid.NewGuid()}, {synced}, DATE '2026-10-11', true),
                       ({Guid.NewGuid()}, {noFeed}, DATE '2026-10-10', false);
                """);

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var days = await after.SupplierAvailability.AsNoTracking().ToListAsync();
        Assert.Equal(3, days.Count);
        Assert.All(days, d => Assert.Equal(SupplierAvailabilitySource.Manual, d.Source));
        var states = await after.SupplierProfiles.AsNoTracking().ToDictionaryAsync(sp => sp.OrgId, sp => sp.CalendarSyncStatus);
        Assert.Equal(SupplierCalendarSyncStatus.Failure, states[failed]);
        Assert.Equal(SupplierCalendarSyncStatus.Success, states[synced]);
        Assert.Equal(SupplierCalendarSyncStatus.None, states[neverSynced]);
        Assert.Equal(SupplierCalendarSyncStatus.None, states[noFeed]);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());

        // The prudent side of the choice: a valid feed without events frees no day written before the migration.
        await new CalendarSyncService(
                after,
                new EmptyCalendarClient(),
                ICalTestServices.ImportService(new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero))),
                Mock.Of<IServiceScopeFactory>(),
                NullLogger<CalendarSyncService>.Instance)
            .SyncIcalFeedAsync(synced);

        after.ChangeTracker.Clear();
        Assert.Equal(2, await after.SupplierAvailability.CountAsync(a => a.OrgId == synced));
        Assert.Equal(
            SupplierCalendarSyncStatus.Success,
            (await after.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == synced)).CalendarSyncStatus);
    }

    private static async Task InsertProfileAsync(AppDbContext db, Guid orgId, string? feedUrl, DateTime? lastSyncAt, string? error)
    {
        var slug = "su15-m-" + orgId.ToString("N");
        var email = orgId.ToString("N") + "@su15.test";
        var syncType = feedUrl is null ? (int)CalendarSyncType.None : (int)CalendarSyncType.ICalFeed;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt", "OrgType")
            VALUES ({orgId}, 'Fornitore', {slug}, 0, 'Fornitore', '', true, now(), now(), 1);

            INSERT INTO "SupplierProfiles" ("OrgId", "Status", "LegalName", "Phone", "Email", "CategoriesJson", "ComuniJson",
                                            "PhotoUrlsJson", "CalendarSyncType", "IcalFeedUrl", "CalendarLastSyncAt",
                                            "CalendarSyncError", "CreatedAt", "UpdatedAt")
            VALUES ({orgId}, {(int)SupplierStatus.Active}, 'Fornitore', '', {email}, '[]', '["H501"]', '[]', {syncType},
                    {feedUrl}, {lastSyncAt}, {error}, now(), now());
            """);
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_" + nameof(AddSupplierCalendarSyncSource), StringComparison.Ordinal));
        Assert.True(index > 0, "AddSupplierCalendarSyncSource migration not found.");
        return all[index - 1];
    }

    private sealed class EmptyCalendarClient : ISafeExternalHttpClient
    {
        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\nEND:VCALENDAR\r\n");
    }
}
