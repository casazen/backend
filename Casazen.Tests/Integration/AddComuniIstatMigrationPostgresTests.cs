using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-04 migration (<c>AddComuniIstat</c>) on real PostgreSQL (A4-12, A5-34, A8-24): the new tables start empty, the new
/// columns are nullable (nothing is inferred for the existing rows), and the SEO pages and signup attributions stored with the
/// four wrong codes of the removed hardcoded registry (Torino, Bellagio, Menaggio, Varenna) move to the official ISTAT codes,
/// without touching a page of another comune or one that would collide with an existing page. The rows are written with raw
/// SQL on the schema right before the migration.
/// </summary>
public class AddComuniIstatMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime Moment = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_WrongRegistryCodes_MoveToTheOfficialOnes_AndNothingElseChanges()
    {
        var torinoGuide = Guid.NewGuid();
        var torinoTax = Guid.NewGuid();
        var genovaPage = Guid.NewGuid();
        var bellagioGuide = Guid.NewGuid();
        var menaggioGuide = Guid.NewGuid();
        var menaggioTax = Guid.NewGuid();
        var menaggioAlreadyThere = Guid.NewGuid();
        var varennaGuide = Guid.NewGuid();
        var comoGuide = Guid.NewGuid();
        var attributions = new Dictionary<string, Guid>
        {
            ["010025"] = Guid.NewGuid(),
            ["013040"] = Guid.NewGuid(),
            ["013133"] = Guid.NewGuid(),
            ["013182"] = Guid.NewGuid(),
            ["013075"] = Guid.NewGuid(),
        };
        var attributionWithoutComune = Guid.NewGuid();

        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));

            // The pages the SEO bootstrap generated with the registry's codes.
            await InsertPageAsync(db, torinoGuide, "010025", "affitti-brevi/piemonte/torino", pageType: 0);
            await InsertPageAsync(db, torinoTax, "010025", "tassa-soggiorno/torino", pageType: 1);
            // 010025 is Genova: a page that names it stays where it is.
            await InsertPageAsync(db, genovaPage, "010025", "supplier/genova", pageType: 2);
            await InsertPageAsync(db, bellagioGuide, "013040", "affitti-brevi/lombardia/bellagio", pageType: 0);
            await InsertPageAsync(db, menaggioGuide, "013133", "affitti-brevi/lombardia/menaggio", pageType: 0);
            await InsertPageAsync(db, menaggioTax, "013133", "tassa-soggiorno/menaggio", pageType: 1);
            // A page of the same type already on the official code (never written by the code, but the unique index must hold).
            await InsertPageAsync(db, menaggioAlreadyThere, "013145", "affitti-brevi/lombardia/menaggio-gia-presente", pageType: 0);
            await InsertPageAsync(db, varennaGuide, "013182", "affitti-brevi/lombardia/varenna", pageType: 0);
            await InsertPageAsync(db, comoGuide, "013075", "affitti-brevi/lombardia/como", pageType: 0);

            foreach (var (code, id) in attributions)
                await InsertAttributionAsync(db, id, code);
            await InsertAttributionAsync(db, attributionWithoutComune, comuneCode: null);

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var pages = await after.SeoContentPages.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.ComuneCode);
        Assert.Equal("001272", pages[torinoGuide]);
        Assert.Equal("001272", pages[torinoTax]);
        Assert.Equal("010025", pages[genovaPage]);
        Assert.Equal("013250", pages[bellagioGuide]);
        Assert.Equal("013145", pages[menaggioTax]);
        // Not moved: the official code already has a page of this type.
        Assert.Equal("013133", pages[menaggioGuide]);
        Assert.Equal("013145", pages[menaggioAlreadyThere]);
        Assert.Equal("097084", pages[varennaGuide]);
        Assert.Equal("013075", pages[comoGuide]);

        var stored = await after.SignupAttributions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.ComuneCode);
        Assert.Equal("001272", stored[attributions["010025"]]);
        Assert.Equal("013250", stored[attributions["013040"]]);
        Assert.Equal("013145", stored[attributions["013133"]]);
        Assert.Equal("097084", stored[attributions["013182"]]);
        Assert.Equal("013075", stored[attributions["013075"]]);
        Assert.Null(stored[attributionWithoutComune]);

        // The new tables start empty: nothing is invented, the official list comes from the seed file or an admin upload.
        Assert.Equal(0, await after.Comuni.CountAsync());
        Assert.Equal(0, await after.ComuneImports.CountAsync());
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migration_CorrectedCodes_AreTheOnesOfTheOfficialList()
    {
        // The codes written in the migration are checked against rows copied verbatim from the official ISTAT file.
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        await ComuneTestData.ImportSampleAsync(db);

        var official = await db.Comuni.AsNoTracking()
            .Where(c => new[] { "Torino", "Bellagio", "Menaggio", "Varenna" }.Contains(c.Name))
            .ToDictionaryAsync(c => c.Name, c => c.IstatCode);

        Assert.Equal("001272", official["Torino"]);
        Assert.Equal("013250", official["Bellagio"]);
        Assert.Equal("013145", official["Menaggio"]);
        Assert.Equal("097084", official["Varenna"]);
    }

    [PostgresFact]
    public async Task Migration_NewColumns_AreNullableAndTheSupplierCodesStartAsAnEmptyList()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        var columns = await db.Database
            .SqlQuery<ColumnInfo>($"""
                SELECT table_name AS "Table", column_name AS "Name", is_nullable AS "Nullable", column_default AS "DefaultValue"
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND ((table_name = 'Properties' AND column_name IN ('ComuneIstatCode', 'RegionCode'))
                    OR (table_name = 'SupplierProfiles' AND column_name = 'ComuneIstatCodesJson'))
                """)
            .ToListAsync();

        Assert.Equal(3, columns.Count);
        var property = columns.Where(c => c.Table == "Properties").ToList();
        Assert.All(property, c => Assert.Equal("YES", c.Nullable));
        var supplier = Assert.Single(columns, c => c.Table == "SupplierProfiles");
        Assert.Equal("NO", supplier.Nullable);
        Assert.Contains("'[]'", supplier.DefaultValue);
    }

    private sealed record ColumnInfo(string Table, string Name, string Nullable, string? DefaultValue);

    private static Task InsertPageAsync(AppDbContext db, Guid id, string comuneCode, string slug, int pageType) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SeoContentPages" ("Id", "Slug", "ComuneCode", "RegionCode", "PageType", "Title", "MetaDescription",
                                           "LegalReviewStatus", "CounselRequired", "LastRefreshedAt", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {slug}, {comuneCode}, 'LOM', {pageType}, 'Titolo', 'Meta', 0, true, {Moment}, {Moment}, {Moment});
            """);

    private static async Task InsertAttributionAsync(AppDbContext db, Guid id, string? comuneCode)
    {
        var orgId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({orgId}, 'Org SU-04', {"su04-" + orgId.ToString("N")}, 0, 'Org SU-04', '', true, now(), now());
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SignupAttributions" ("Id", "OrgId", "ComuneCode", "RecordedAt")
            VALUES ({id}, {orgId}, {comuneCode}, {Moment});
            """);
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddComuniIstat", StringComparison.Ordinal));
        Assert.True(index > 0, "AddComuniIstat migration not found.");
        return all[index - 1];
    }
}
