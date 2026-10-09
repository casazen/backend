using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-05 migration (<see cref="AddSupplierBusyWindowFeedKey"/>) on real PostgreSQL: it applies and reverts, it applies over a table
/// that already holds windows (the ones set by hand, with no UID, and engagements of a feed), and once applied the index refuses
/// the same occurrence of a supplier twice while leaving the windows with no UID alone.
/// </summary>
public class AddSupplierBusyWindowFeedKeyPostgresTests : IAsyncLifetime
{
    private const string IndexName = "UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc";

    private static readonly DateTime Ten = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_AppliesAndRevertsOnARealDatabase()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(1, await CountIndexesAsync(db));

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        Assert.Equal(0, await CountIndexesAsync(db));

        await db.Database.MigrateAsync();
        Assert.Equal(1, await CountIndexesAsync(db));
    }

    [PostgresFact]
    public async Task Migration_OverATableThatHoldsWindows_Applies_AndIsPartial()
    {
        await using var db = _database!.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        var org = await SeedSupplierAsync(db);
        // What exists before SP-05: windows set by hand, no UID, even two at the same hour.
        db.SupplierBusyWindows.Add(Window(org, null, Ten, SupplierBusyWindowKind.Block, SupplierBusyWindowSource.Manual));
        db.SupplierBusyWindows.Add(Window(org, null, Ten, SupplierBusyWindowKind.Block, SupplierBusyWindowSource.Manual));
        db.SupplierBusyWindows.Add(Window(org, null, Ten.AddDays(1), SupplierBusyWindowKind.ExtraOpening, SupplierBusyWindowSource.Manual));
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync();

        Assert.Equal(1, await CountIndexesAsync(db));
        var definition = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {IndexName}")
            .SingleAsync();
        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(\"OrgId\", \"ExternalUid\", \"StartUtc\")", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (\"ExternalUid\" IS NOT NULL)", definition, StringComparison.Ordinal);
        Assert.Equal(3, await db.SupplierBusyWindows.CountAsync());
    }

    [PostgresFact]
    public async Task Index_RefusesTheSameOccurrenceOfASupplierTwice_AndNothingElse()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var orgA = await SeedSupplierAsync(db);
        var orgB = await SeedSupplierAsync(db);
        db.SupplierBusyWindows.Add(Window(orgA, "uid", Ten, SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed));
        await db.SaveChangesAsync();

        // Same supplier, UID and start: refused (also with another end or another kind of window).
        await using (var duplicate = _database.CreateContext())
        {
            duplicate.SupplierBusyWindows.Add(Window(orgA, "uid", Ten, SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed, hours: 4));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(IndexName, postgres.ConstraintName);
        }

        // Another start, another UID, another supplier, and any number of windows with no UID: all accepted.
        db.SupplierBusyWindows.Add(Window(orgA, "uid", Ten.AddDays(1), SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed));
        db.SupplierBusyWindows.Add(Window(orgA, "other", Ten, SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed));
        db.SupplierBusyWindows.Add(Window(orgB, "uid", Ten, SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed));
        db.SupplierBusyWindows.Add(Window(orgA, null, Ten, SupplierBusyWindowKind.Block, SupplierBusyWindowSource.Manual));
        db.SupplierBusyWindows.Add(Window(orgA, null, Ten, SupplierBusyWindowKind.Block, SupplierBusyWindowSource.Manual));
        await db.SaveChangesAsync();
        Assert.Equal(6, await db.SupplierBusyWindows.CountAsync());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddSupplierBusyWindowFeedKey", StringComparison.Ordinal));
        Assert.True(index > 0, "AddSupplierBusyWindowFeedKey migration not found.");
        return all[index - 1];
    }

    private static async Task<int> CountIndexesAsync(AppDbContext db) =>
        (int)await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_indexes WHERE indexname = {IndexName}")
            .SingleAsync();

    private static async Task<Guid> SeedSupplierAsync(AppDbContext db)
    {
        var email = $"sp05.{Guid.NewGuid():N}@example.com";
        var org = new OrgEntity
        {
            Name = "Fornitore SP-05",
            Slug = $"sp05-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SP-05",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();

        // Plain SQL: the first test seeds on the schema of the migration before this one, which lacks the columns that later
        // migrations add to the profile (the current model would write them).
        await SupplierProfileSql.InsertAsync(db, new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-05 Srl",
            Phone = "+39 06 050505",
            CategoriesJson = "[]",
            ComuniJson = """["H501"]""",
        });
        return org.Id;
    }

    private static SupplierBusyWindow Window(
        Guid orgId,
        string? uid,
        DateTime start,
        SupplierBusyWindowKind kind,
        SupplierBusyWindowSource source,
        int hours = 1) =>
        new()
        {
            OrgId = orgId,
            StartUtc = start,
            EndUtc = start.AddHours(hours),
            Kind = kind,
            Source = source,
            ExternalUid = uid,
        };
}
