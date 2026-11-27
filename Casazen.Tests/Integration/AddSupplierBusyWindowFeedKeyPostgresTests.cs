using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-05 migration (<c>AddSupplierBusyWindowFeedKey</c>) on real PostgreSQL, on the schema as it is now (a copy of the migrated
/// template): the index is unique on the supplier, the UID and the start and only covers the windows that have a UID, and it refuses
/// the same occurrence of a supplier twice while leaving the windows with no UID alone. The apply and revert of the migration, and
/// its apply over a table that already held windows, ran when the release that carried it went to production: they are not replayed.
/// </summary>
public class AddSupplierBusyWindowFeedKeyPostgresTests : IAsyncLifetime
{
    private const string IndexName = "UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc";

    private static readonly DateTime Ten = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Index_IsUniqueOnTheSupplierTheUidAndTheStart_AndIsPartial()
    {
        await using var db = _database!.CreateContext();

        Assert.Equal(1, await CountIndexesAsync(db));
        var definition = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {IndexName}")
            .SingleAsync();
        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(\"OrgId\", \"ExternalUid\", \"StartUtc\")", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (\"ExternalUid\" IS NOT NULL)", definition, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Index_RefusesTheSameOccurrenceOfASupplierTwice_AndNothingElse()
    {
        await using var db = _database!.CreateContext();
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
        // Plain SQL: the org and the profile are written with the helpers that name only the columns they need, so a column that
        // a later migration adds to the org (DB-03: HostName, PublicPhone, Subtitle) or to the profile cannot break this test.
        await LegacyOrgRows.InsertAsync(db, org);
        await db.Database.ExecuteSqlAsync($"""UPDATE "Orgs" SET "OrgType" = {(int)org.OrgType} WHERE "Id" = {org.Id}""");
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
