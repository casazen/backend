using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-04 on a real PostgreSQL database: the official ISTAT file shipped with the build (<c>Data/Seeds/comuni-istat.csv</c>,
/// 21/02/2026), the unique indexes, the order of the writes of a province change and the lock that serializes two imports.
/// </summary>
public class ComuneImportPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Seed_OfficialFileOfTheBuild_ImportsEveryComuneOnce()
    {
        await using var db = MigratedContext();
        var service = ComuneTestData.CreateImportService(db);

        var outcome = await service.ImportSeedIfNewerAsync();

        Assert.Equal(ComuneSeedOutcome.Imported, outcome);
        db.ChangeTracker.Clear();
        var comuni = await db.Comuni.AsNoTracking().ToListAsync();
        Assert.Equal(7894, comuni.Count);
        Assert.All(comuni, c => Assert.True(c.IsActive));
        Assert.Equal(7894, comuni.Select(c => c.IstatCode).Distinct().Count());
        Assert.All(comuni, c => Assert.Matches("^[0-9]{6}$", c.IstatCode));
        Assert.All(comuni, c => Assert.True(c.CadastralCode is null || ComuneRules.IsCadastralCode(c.CadastralCode)));
        Assert.Equal(7894, comuni.Select(c => c.CadastralCode).Distinct().Count());
        Assert.All(comuni, c => Assert.NotNull(c.RegionCode));
        Assert.Equal(20, comuni.Select(c => c.RegionIstatCode).Distinct().Count());
        Assert.Equal(110, comuni.Select(c => c.ProvinceCode).Distinct().Count());

        // The import is on record, with the source the product owner supplied.
        var import = await db.ComuneImports.AsNoTracking().SingleAsync();
        Assert.Equal((ComuneImportOrigin.StartupSeed, "comuni-istat.csv", new DateOnly(2026, 2, 21), 7894, 7894, "system"), (import.Origin, import.SourceFileName, import.ReferenceDate, import.RowCount, import.InsertedCount, import.ImportedBy));
        Assert.Contains("ISTAT", import.SourceVersion);
        Assert.Equal("b90e79d45c2a81be38657ff37b76b013b23521b14aadb8382efd56273ac1a93b", import.Sha256);
    }

    [PostgresFact]
    public async Task Seed_AgainOrOverANewerList_LeavesTheDatabaseAlone()
    {
        await using var db = MigratedContext();
        var service = ComuneTestData.CreateImportService(db);
        await service.ImportSeedIfNewerAsync();

        Assert.Equal(ComuneSeedOutcome.AlreadyUpToDate, await service.ImportSeedIfNewerAsync());
        db.ChangeTracker.Clear();
        Assert.Single(await db.ComuneImports.ToListAsync());

        // An admin imported a later list (here: the sample dated later): the older seed file never replaces it.
        var directory = new ComuneDirectory(db);
        await using (var stream = ComuneTestData.OpenSample())
        {
            var newer = await service.ImportAsync(
                new ComuneImportRequest("later.csv", "later list", new DateOnly(2026, 9, 1), "admin", IsPartial: true), stream);
            Assert.True(newer.Success);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(ComuneSeedOutcome.AlreadyUpToDate, await service.ImportSeedIfNewerAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), (await directory.GetStatusAsync()).LastImport!.ReferenceDate);
    }

    [PostgresFact]
    public async Task Import_OfficialFileAgain_ChangesNothing()
    {
        await using var db = MigratedContext();
        var service = ComuneTestData.CreateImportService(db);
        await service.ImportSeedIfNewerAsync();
        db.ChangeTracker.Clear();

        await using var stream = OpenSeed();
        var again = await service.ImportAsync(new ComuneImportRequest("comuni-istat.csv", "again", new DateOnly(2026, 2, 21), "admin"), stream);

        Assert.True(again.Success);
        Assert.Equal((7894, 0, 0, 7894, 0), (again.Rows, again.Inserted, again.Updated, again.Unchanged, again.Deactivated));
    }

    [PostgresFact]
    public async Task OfficialFile_AnswersTheCasesTheOldRegistryGotWrong()
    {
        await using var db = MigratedContext();
        await ComuneTestData.CreateImportService(db).ImportSeedIfNewerAsync();
        var directory = new ComuneDirectory(db);

        var resolved = await directory.ResolveAsync(["F205", "D612", "H501", "L219", "D969", "Torino", "Genova", "Varenna", "Bellagio", "Menaggio"]);

        Assert.Equal("015146", resolved["F205"].IstatCode); // Milano, not Firenze
        Assert.Equal("048017", resolved["D612"].IstatCode); // Firenze
        Assert.Equal("058091", resolved["H501"].IstatCode);
        Assert.Equal("001272", resolved["L219"].IstatCode); // Torino
        Assert.Equal("010025", resolved["D969"].IstatCode); // Genova
        Assert.Equal("001272", resolved["Torino"].IstatCode);
        Assert.Equal("010025", resolved["Genova"].IstatCode);
        Assert.Equal(("097084", "LC", "LOM"), (resolved["Varenna"].IstatCode, resolved["Varenna"].ProvinceCode, resolved["Varenna"].RegionCode));
        Assert.Equal("013250", resolved["Bellagio"].IstatCode);
        Assert.Equal("013145", resolved["Menaggio"].IstatCode);

        // Every pilot comune of the SEO pages is in the list under its own name and province.
        Assert.Equal(SeoPilotComuni.All.Count, (await directory.FindByNamesAndProvincesAsync(SeoPilotComuni.All)).Count);
    }

    [PostgresFact]
    public async Task OfficialFile_AllRegionsAgreeWithTheClosedTableOfRegions()
    {
        await using var db = MigratedContext();
        await ComuneTestData.CreateImportService(db).ImportSeedIfNewerAsync();

        var regions = await db.Comuni.AsNoTracking().Select(c => new { c.RegionIstatCode, c.RegionName }).Distinct().ToListAsync();

        Assert.Equal(20, regions.Count);
        Assert.All(regions, r => Assert.Equal(r.RegionIstatCode, ItalianRegions.FindByName(r.RegionName)?.IstatCode));
    }

    [PostgresFact]
    public async Task UniqueIndexes_AreEnforcedByTheDatabase_OnlyAmongActiveCadastralCodes()
    {
        await using var db = MigratedContext();
        await ComuneTestData.ImportSampleAsync(db);
        var importId = (await db.ComuneImports.AsNoTracking().SingleAsync()).Id;

        // Two active comuni with one cadastral code: refused by the filtered unique index.
        db.Comuni.Add(Comune("999001", "H501", active: true, importId));
        var violation = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)violation.InnerException!).SqlState);
        db.ChangeTracker.Clear();

        // The same code on a deactivated row is allowed (a comune that changed province), and the ISTAT code is the key.
        db.Comuni.Add(Comune("999001", "H501", active: false, importId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Comuni.Add(Comune("999001", "Z999", active: true, importId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Import_ComuneChangesProvince_DeactivatesTheOldRowBeforeInsertingTheNewOne()
    {
        await using var db = MigratedContext();
        var service = ComuneTestData.CreateImportService(db);
        var header = "Codice Regione;Codice Comune formato alfanumerico;Denominazione in italiano;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune";

        Assert.True((await ImportTextAsync(service, header + "\n03;999001;Prova Uno;Lombardia;MI;Z901\n")).Success);
        var moved = await ImportTextAsync(service, header + "\n03;888001;Prova Uno;Lombardia;MB;Z901\n");

        Assert.True(moved.Success, string.Join(",", moved.Errors));
        Assert.Equal((1, 1), (moved.Inserted, moved.Deactivated));
        db.ChangeTracker.Clear();
        Assert.Equal("888001", (await db.Comuni.AsNoTracking().SingleAsync(c => c.IsActive)).IstatCode);
        Assert.False((await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999001")).IsActive);
    }

    [PostgresFact]
    public async Task Import_TwoAtOnce_AreSerializedAndTheSecondChangesNothing()
    {
        // Two instances starting together (or an admin and a starting instance): one writes, the other finds it done.
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = MigratedContext();
            await using var stream = ComuneTestData.OpenSample();
            return await ComuneTestData.CreateImportService(db).ImportAsync(
                new ComuneImportRequest("sample.csv", "sample", ComuneTestData.ReferenceDate, "admin"), stream);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success, string.Join(",", r.Errors)));
        Assert.Equal(29, results.Sum(r => r.Inserted));
        Assert.Equal(29, results.Sum(r => r.Unchanged));
        await using var check = MigratedContext();
        Assert.Equal(29, await check.Comuni.CountAsync());
        Assert.Equal(2, await check.ComuneImports.CountAsync());
    }

    [PostgresFact]
    public async Task Properties_AndSuppliers_StartWithoutAComune_AndTheColumnsDefaultToEmpty()
    {
        await using var db = MigratedContext();

        var columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ':' || coalesce(column_default, '-') AS "Value"
            FROM information_schema.columns
            WHERE table_name IN ('Properties', 'SupplierProfiles')
              AND column_name IN ('ComuneIstatCode', 'RegionCode', 'ComuneIstatCodesJson')
            ORDER BY column_name
            """).ToListAsync();

        Assert.Equal(3, columns.Count);
        Assert.Contains(columns, c => c.StartsWith("ComuneIstatCode:-", StringComparison.Ordinal));
        Assert.Contains(columns, c => c.StartsWith("RegionCode:-", StringComparison.Ordinal));
        Assert.Contains(columns, c => c.StartsWith("ComuneIstatCodesJson:'[]'", StringComparison.Ordinal));
    }

    private static Comune Comune(string istat, string cadastral, bool active, Guid importId) => new()
    {
        IstatCode = istat,
        CadastralCode = cadastral,
        Name = "Prova",
        DisplayName = "Prova",
        NormalizedName = "prova",
        SearchText = "prova",
        ProvinceCode = "MI",
        RegionIstatCode = "03",
        RegionName = "Lombardia",
        IsActive = active,
        SourceImportId = importId,
    };

    private static async Task<ComuneImportResult> ImportTextAsync(ComuneImportService service, string text)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        return await service.ImportAsync(new ComuneImportRequest("t.csv", "synthetic rows", ComuneTestData.ReferenceDate, "test"), stream);
    }

    private static Stream OpenSeed() =>
        typeof(ComuneImportService).Assembly.GetManifestResourceStream(ComuneImportService.SeedResourceName)
        ?? throw new InvalidOperationException("The seed file is not part of the build.");

    private readonly object _migrationGate = new();
    private bool _migrated;

    /// <summary>A new context on the database, migrated once.</summary>
    private AppDbContext MigratedContext()
    {
        lock (_migrationGate)
        {
            if (!_migrated)
            {
                using var migrator = _database!.CreateContext();
                migrator.Database.Migrate();
                _migrated = true;
            }
        }

        return _database!.CreateContext();
    }
}
