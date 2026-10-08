using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SU-04: import of the official ISTAT comuni list. The rows of the official file come from the sample fixture (29 rows
/// copied verbatim); the rows written in the tests are synthetic and say so: the comuni are named "Prova" and the codes
/// are not real ones (province prefix 999).
/// </summary>
public class ComuneImportServiceTests
{
    private const string OfficialHeader =
        "Codice Regione;Codice Comune formato alfanumerico;Denominazione (Italiana e straniera);Denominazione in italiano;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune";

    private static readonly DateOnly Reference = ComuneTestData.ReferenceDate;

    // ---- Official rows ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportAsync_OfficialSample_ImportsEveryRowWithItsFields()
    {
        await using var db = CreateDb();

        var result = await ComuneTestData.ImportSampleAsync(db);

        Assert.True(result.Success);
        Assert.Equal((29, 29, 0, 0, 0), (result.Rows, result.Inserted, result.Updated, result.Unchanged, result.Deactivated));

        var roma = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == ComuneTestData.Roma);
        Assert.Equal(("H501", "Roma", "RM", "12", "Lazio", "LAZ", true), (roma.CadastralCode, roma.Name, roma.ProvinceCode, roma.RegionIstatCode, roma.RegionName, roma.RegionCode, roma.IsActive));
        Assert.Equal("roma", roma.NormalizedName);

        // Leading zeros survive (the code is text), and the bilingual denomination is kept next to the Italian name.
        var torino = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == ComuneTestData.Torino);
        Assert.Equal(("L219", "TO", "PIE"), (torino.CadastralCode, torino.ProvinceCode, torino.RegionCode));
        var bolzano = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == ComuneTestData.Bolzano);
        Assert.Equal(("Bolzano", "Bolzano/Bozen", "bolzano bozen"), (bolzano.Name, bolzano.DisplayName, bolzano.SearchText));
        Assert.Equal("TAA", bolzano.RegionCode);
    }

    [Fact]
    public async Task ImportAsync_OfficialSample_KnownErrorsOfTheOldRegistryAreCorrect()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        // F205 is Milano (the old registry said Firenze, which is D612); 010025 is Genova (the old registry said Torino,
        // which is 001272); Varenna is in the province of Lecco with a code of that province.
        Assert.Equal("Milano", (await db.Comuni.SingleAsync(c => c.CadastralCode == "F205")).Name);
        Assert.Equal("Firenze", (await db.Comuni.SingleAsync(c => c.CadastralCode == "D612")).Name);
        Assert.Equal("Genova", (await db.Comuni.SingleAsync(c => c.IstatCode == "010025")).Name);
        Assert.Equal("Torino", (await db.Comuni.SingleAsync(c => c.IstatCode == "001272")).Name);
        var varenna = await db.Comuni.SingleAsync(c => c.Name == "Varenna");
        Assert.Equal(("097084", "LC", "L680"), (varenna.IstatCode, varenna.ProvinceCode, varenna.CadastralCode));
        Assert.Equal("013250", (await db.Comuni.SingleAsync(c => c.Name == "Bellagio")).IstatCode);
        Assert.Equal("013145", (await db.Comuni.SingleAsync(c => c.Name == "Menaggio")).IstatCode);
    }

    [Fact]
    public async Task ImportAsync_SameFileTwice_ChangesNothing()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var before = await db.Comuni.AsNoTracking().OrderBy(c => c.IstatCode).Select(c => new { c.IstatCode, c.UpdatedAt, c.SourceImportId }).ToListAsync();

        var again = await ComuneTestData.ImportSampleAsync(db);

        Assert.True(again.Success);
        Assert.Equal((29, 0, 0, 29, 0), (again.Rows, again.Inserted, again.Updated, again.Unchanged, again.Deactivated));
        var after = await db.Comuni.AsNoTracking().OrderBy(c => c.IstatCode).Select(c => new { c.IstatCode, c.UpdatedAt, c.SourceImportId }).ToListAsync();
        Assert.Equal(before, after);

        // Both imports are logged, with the checksum of the file.
        var imports = await db.ComuneImports.AsNoTracking().ToListAsync();
        Assert.Equal(2, imports.Count);
        Assert.Single(imports.Select(i => i.Sha256).Distinct());
        Assert.All(imports, i => Assert.Equal((ComuneImportOrigin.AdminUpload, Reference, 64), (i.Origin, i.ReferenceDate, i.Sha256.Length)));
    }

    // ---- Upsert, deactivation, validity -----------------------------------------------------------------------

    [Fact]
    public async Task ImportAsync_RowChanged_UpdatesOnlyThatRow()
    {
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"), Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z902"));

        var result = await ImportAsync(db, Row("03", "999001", "Prova Uno Nuovo", "Lombardia", "MI", "Z901"), Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z902"));

        Assert.True(result.Success);
        Assert.Equal((0, 1, 1, 0), (result.Inserted, result.Updated, result.Unchanged, result.Deactivated));
        Assert.Equal("Prova Uno Nuovo", (await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999001")).Name);
        // The changed row points at the import that changed it, the other one at the first import.
        var imports = await db.ComuneImports.AsNoTracking().OrderBy(i => i.ImportedAt).ToListAsync();
        Assert.Equal(imports[1].Id, (await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999001")).SourceImportId);
        Assert.Equal(imports[0].Id, (await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999002")).SourceImportId);
    }

    [Fact]
    public async Task ImportAsync_FullListWithoutAComune_DeactivatesItAndItStaysResolvable()
    {
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"), Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z902"));

        var result = await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"));

        Assert.Equal((0, 0, 1, 1), (result.Inserted, result.Updated, result.Unchanged, result.Deactivated));
        var gone = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999002");
        Assert.False(gone.IsActive);
        Assert.Equal((DateOnly?)Reference, gone.ValidTo);

        var directory = new ComuneDirectory(db);
        Assert.Null(await directory.FindByIstatCodeAsync("999002"));
        Assert.NotNull(await directory.FindByIstatCodeAsync("999002", activeOnly: false));
        Assert.DoesNotContain(await directory.SearchAsync("Prova", 10), c => c.IstatCode == "999002");
    }

    [Fact]
    public async Task ImportAsync_PartialFile_LeavesTheOtherComuniActive()
    {
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"), Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z902"));

        var result = await ImportAsync(db, partial: true, Row("03", "999003", "Prova Tre", "Lombardia", "MI", "Z903"));

        Assert.Equal((1, 0, 0, 0), (result.Inserted, result.Updated, result.Unchanged, result.Deactivated));
        Assert.Equal(3, await db.Comuni.CountAsync(c => c.IsActive));
    }

    [Fact]
    public async Task ImportAsync_ComuneBackInTheList_IsReactivated()
    {
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"), Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z902"));
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"));

        var result = await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"), Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z902"));

        Assert.Equal((0, 1, 1, 0), (result.Inserted, result.Updated, result.Unchanged, result.Deactivated));
        var back = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999002");
        Assert.True(back.IsActive);
        Assert.Null(back.ValidTo);
    }

    [Fact]
    public async Task ImportAsync_ComuneChangesProvince_KeepsItsCadastralCodeOnTheNewRow()
    {
        // A comune that moves to another province gets a new ISTAT code and keeps its cadastral code: the old row is
        // deactivated before the new one is inserted, so the unique cadastral code of the active comuni is never broken.
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"));

        var result = await ImportAsync(db, Row("03", "888001", "Prova Uno", "Lombardia", "MB", "Z901"));

        Assert.True(result.Success);
        Assert.Equal((1, 0, 0, 1), (result.Inserted, result.Updated, result.Unchanged, result.Deactivated));
        Assert.Equal("888001", (await new ComuneDirectory(db).ResolveAsync(["Z901"]))["Z901"].IstatCode);
    }

    [Fact]
    public async Task ImportAsync_ValidityDates_AComuneThatCeasedBeforeTheListIsStoredInactive()
    {
        await using var db = CreateDb();
        var header = OfficialHeader + ";Data istituzione;Data cessazione";

        var result = await ImportTextAsync(db, header, "03;999001;Prova Uno;Prova Uno;Lombardia;MI;Z901;01/01/1990;", "03;999002;Prova Due;Prova Due;Lombardia;MI;Z902;1990-01-01;31/12/2015");

        Assert.True(result.Success);
        var open = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999001");
        Assert.Equal((true, (DateOnly?)new DateOnly(1990, 1, 1), (DateOnly?)null), (open.IsActive, open.ValidFrom, open.ValidTo));
        var ceased = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999002");
        Assert.Equal((false, (DateOnly?)new DateOnly(2015, 12, 31)), (ceased.IsActive, ceased.ValidTo));
    }

    [Fact]
    public async Task ImportAsync_CadastralCodeNotAvailable_IsStoredAsNull()
    {
        // The legend of the official file: "N.d." = not available (a comune just instituted).
        await using var db = CreateDb();

        var result = await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "N.d."), Row("03", "999002", "Prova Due", "Lombardia", "MI", "n.d."));

        Assert.True(result.Success);
        Assert.All(await db.Comuni.AsNoTracking().ToListAsync(), c => Assert.Null(c.CadastralCode));
    }

    // ---- Tolerated variants of the layout ---------------------------------------------------------------------

    [Fact]
    public async Task ImportAsync_CommaSeparatorAndSynonymHeaders_IsAccepted()
    {
        await using var db = CreateDb();

        var result = await ImportTextAsync(
            db,
            "CODISTAT,DENOMINAZIONE_IT,CODCATASTALE,SIGLAPROVINCIA,REGIONE",
            "999001,Prova Uno,Z901,MI,Lombardia",
            "\"999002\",\"Prova, Due\",Z902,MI,Lombardia");

        Assert.True(result.Success);
        var due = await db.Comuni.AsNoTracking().SingleAsync(c => c.IstatCode == "999002");
        Assert.Equal(("Prova, Due", "03", "LOM"), (due.Name, due.RegionIstatCode, due.RegionCode));
    }

    [Fact]
    public async Task ImportAsync_NumericCodeWithoutLeadingZeros_GetsThemBack()
    {
        // A spreadsheet that dropped the leading zeros, or the numeric format of the list (1272 for 001272).
        await using var db = CreateDb();

        var result = await ImportTextAsync(db, "Codice Regione;Codice Comune formato numerico;Denominazione in italiano;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune", "01;1272;Prova;Piemonte;TO;Z901");

        Assert.True(result.Success);
        Assert.Equal("001272", (await db.Comuni.SingleAsync()).IstatCode);
    }

    [Fact]
    public async Task ImportAsync_Windows1252File_IsDecoded()
    {
        await using var db = CreateDb();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes(OfficialHeader + "\n" + Row("08", "999001", "Provalì", "Emilia-Romagna", "FC", "Z901") + "\n");

        var result = await ImportBytesAsync(db, bytes);

        Assert.True(result.Success);
        Assert.Equal("Provalì", (await db.Comuni.SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAsync_Utf8WithByteOrderMarkAndCrLf_IsAccepted()
    {
        await using var db = CreateDb();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(OfficialHeader + "\r\n" + Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901") + "\r\n\r\n"))
            .ToArray();

        var result = await ImportBytesAsync(db, bytes);

        Assert.True(result.Success);
        Assert.Equal(1, await db.Comuni.CountAsync());
    }

    // ---- Rejections: nothing is written ----------------------------------------------------------------------

    [Theory]
    [InlineData("03;999;Prova;Prova;Lombardia;MI;Z901", ComuneImportErrors.IstatInvalid)]
    [InlineData("03;9990011;Prova;Prova;Lombardia;MI;Z901", ComuneImportErrors.IstatInvalid)]
    [InlineData("03;99900A;Prova;Prova;Lombardia;MI;Z901", ComuneImportErrors.IstatInvalid)]
    [InlineData("03;999001;Prova;;Lombardia;MI;Z901", ComuneImportErrors.NameMissing)]
    [InlineData("03;999001;Prova;Prova;Lombardia;MI;", ComuneImportErrors.CadastralInvalid)]
    [InlineData("03;999001;Prova;Prova;Lombardia;MI;Z90", ComuneImportErrors.CadastralInvalid)]
    [InlineData("03;999001;Prova;Prova;Lombardia;MI;9901", ComuneImportErrors.CadastralInvalid)]
    [InlineData("03;999001;Prova;Prova;Lombardia;M;Z901", ComuneImportErrors.ProvinceInvalid)]
    [InlineData("03;999001;Prova;Prova;Lombardia;M1;Z901", ComuneImportErrors.ProvinceInvalid)]
    [InlineData("21;999001;Prova;Prova;Lombardia;MI;Z901", ComuneImportErrors.RegionUnknown)]
    [InlineData("xx;999001;Prova;Prova;Lombardia;MI;Z901", ComuneImportErrors.RegionUnknown)]
    [InlineData("03;999001;Prova;Prova;Lazio;MI;Z901", ComuneImportErrors.RegionInconsistent)]
    [InlineData("03;999001;Prova;Prova;Lombardia;MI;Z901;extra", ComuneImportErrors.ColumnCountMismatch)]
    [InlineData("03;999001;Prova", ComuneImportErrors.ColumnCountMismatch)]
    public async Task ImportAsync_InvalidRow_RejectsTheWholeFileAndWritesNothing(string badRow, string expectedError)
    {
        await using var db = CreateDb();

        var result = await ImportTextAsync(
            db,
            OfficialHeader,
            "03;999100;Valida;Valida;Lombardia;MI;Z910",
            badRow,
            "03;999101;Valida Due;Valida Due;Lombardia;MI;Z911");

        Assert.False(result.Success);
        Assert.Equal(new ComuneImportLineError(3, expectedError), Assert.Single(result.Errors));
        Assert.Empty(await db.Comuni.ToListAsync());
        Assert.Empty(await db.ComuneImports.ToListAsync());
    }

    [Fact]
    public async Task ImportAsync_RepeatedCodes_AreRefused()
    {
        await using var db = CreateDb();

        var result = await ImportTextAsync(
            db,
            OfficialHeader,
            "03;999001;Prova Uno;Prova Uno;Lombardia;MI;Z901",
            "03;999001;Prova Uno bis;Prova Uno bis;Lombardia;MI;Z902",
            "03;999003;Prova Tre;Prova Tre;Lombardia;MI;Z901");

        Assert.Equal(
            [new ComuneImportLineError(3, ComuneImportErrors.DuplicateIstatCode), new ComuneImportLineError(4, ComuneImportErrors.DuplicateCadastralCode)],
            result.Errors);
        Assert.Empty(await db.Comuni.ToListAsync());
    }

    [Fact]
    public async Task ImportAsync_PartialFileWithACadastralCodeOfAnActiveComune_IsRefusedWithoutAnyChange()
    {
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"));

        var result = await ImportAsync(db, partial: true, Row("03", "999002", "Prova Due", "Lombardia", "MI", "Z901"));

        Assert.False(result.Success);
        Assert.Equal(ComuneImportErrors.ConflictsWithStoredComuni, Assert.Single(result.Errors).Error);
        Assert.Equal(["999001"], await db.Comuni.AsNoTracking().Select(c => c.IstatCode).ToListAsync());
    }

    [Theory]
    [InlineData("Codice Regione;Denominazione in italiano;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune", ComuneImportErrors.IstatColumnMissing)]
    [InlineData("Codice Regione;Codice Comune formato alfanumerico;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune", ComuneImportErrors.NameColumnMissing)]
    [InlineData("Codice Regione;Codice Comune formato alfanumerico;Denominazione in italiano;Denominazione Regione;Sigla automobilistica", ComuneImportErrors.CadastralColumnMissing)]
    [InlineData("Codice Regione;Codice Comune formato alfanumerico;Denominazione in italiano;Denominazione Regione;Codice Catastale del Comune", ComuneImportErrors.ProvinceColumnMissing)]
    [InlineData("Codice Comune formato alfanumerico;Denominazione in italiano;Sigla automobilistica;Codice Catastale del Comune", ComuneImportErrors.RegionColumnMissing)]
    public async Task ImportAsync_HeaderWithoutARequiredColumn_IsRefusedByName(string header, string expectedError)
    {
        await using var db = CreateDb();

        var result = await ImportTextAsync(db, header, "03;999001;Prova;Lombardia;MI;Z901");

        Assert.Equal(expectedError, Assert.Single(result.Errors).Error);
        Assert.Empty(await db.Comuni.ToListAsync());
    }

    [Fact]
    public async Task ImportAsync_ReferenceDateOlderThanTheImportedList_IsRefused()
    {
        await using var db = CreateDb();
        await ImportAsync(db, Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901"));

        var result = await ImportAsync(db, Reference.AddDays(-1), partial: false, Row("03", "999001", "Prova Uno Vecchio", "Lombardia", "MI", "Z901"));

        Assert.Equal(ComuneImportErrors.ReferenceDateOlderThanCurrent, Assert.Single(result.Errors).Error);
        Assert.Equal("Prova Uno", (await db.Comuni.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAsync_ReferenceDateInTheFuture_IsRefused()
    {
        await using var db = CreateDb();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var service = ComuneTestData.CreateImportService(db, clock);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(OfficialHeader + "\n" + Row("03", "999001", "Prova Uno", "Lombardia", "MI", "Z901")));
        var result = await service.ImportAsync(new ComuneImportRequest("f.csv", "test", new DateOnly(2026, 3, 2), "test"), stream);

        Assert.Equal(ComuneImportErrors.ReferenceDateInFuture, Assert.Single(result.Errors).Error);
    }

    [Fact]
    public async Task ImportAsync_NoSourceVersion_IsRefused()
    {
        await using var db = CreateDb();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(OfficialHeader));

        var result = await ComuneTestData.CreateImportService(db).ImportAsync(new ComuneImportRequest("f.csv", "  ", Reference, "test"), stream);

        Assert.Equal(ComuneImportErrors.SourceVersionMissing, Assert.Single(result.Errors).Error);
    }

    [Fact]
    public async Task ImportAsync_EmptyFileOrHeaderOnly_IsRefused()
    {
        await using var db = CreateDb();

        Assert.Equal(ComuneImportErrors.EmptyFile, Assert.Single((await ImportBytesAsync(db, [])).Errors).Error);
        Assert.Equal(ComuneImportErrors.EmptyFile, Assert.Single((await ImportBytesAsync(db, Encoding.UTF8.GetBytes("\n  \n"))).Errors).Error);
        Assert.Equal(ComuneImportErrors.NoRows, Assert.Single((await ImportBytesAsync(db, Encoding.UTF8.GetBytes(OfficialHeader + "\n"))).Errors).Error);
    }

    [Fact]
    public async Task ImportAsync_ManyInvalidRows_ListsOnlyTheFirstErrors()
    {
        await using var db = CreateDb();
        var rows = Enumerable.Range(0, 80).Select(i => $"03;{i};Prova;Prova;Lombardia;MI;Z901").ToArray();

        var result = await ImportTextAsync(db, OfficialHeader, rows);

        Assert.Equal(ComuneImportService.MaxReportedErrors, result.Errors.Count);
    }

    // ---- Regions ---------------------------------------------------------------------------------------------

    [Fact]
    public void ItalianRegions_AreTheTwentyOfTheIstatNumbering()
    {
        Assert.Equal(20, ItalianRegions.All.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => i.ToString("00")), ItalianRegions.All.Select(r => r.IstatCode));
        Assert.Equal(20, ItalianRegions.All.Select(r => r.Code).Distinct().Count());
        Assert.Equal(20, ItalianRegions.All.Select(r => r.Slug).Distinct().Count());
        // The codes the regional configuration already used keep their meaning.
        Assert.Equal("Lombardia", ItalianRegions.FindByCode("LOM")!.Name);
        Assert.Equal("Lazio", ItalianRegions.FindByCode("laz")!.Name);
    }

    [Theory]
    [InlineData("Lombardia", "03")]
    [InlineData("Valle d'Aosta/Vallée d'Aoste", "02")]
    [InlineData("Trentino-Alto Adige/Südtirol", "04")]
    [InlineData("Friuli-Venezia Giulia", "06")]
    [InlineData("emilia romagna", "08")]
    public void ItalianRegions_FindByName_AcceptsTheWordingOfTheOfficialList(string name, string istatCode) =>
        Assert.Equal(istatCode, ItalianRegions.FindByName(name)?.IstatCode);

    [Fact]
    public void ItalianRegions_FindByName_UnknownRegionIsNotGuessed()
    {
        Assert.Null(ItalianRegions.FindByName("Lombardy"));
        Assert.Null(ItalianRegions.FindByName(""));
        Assert.Null(ItalianRegions.FindByName("Lomb"));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static string Row(string regionCode, string istat, string name, string regionName, string province, string cadastral) =>
        $"{regionCode};{istat};{name};{name};{regionName};{province};{cadastral}";

    private static Task<ComuneImportResult> ImportAsync(AppDbContext db, params string[] rows) =>
        ImportAsync(db, Reference, partial: false, rows);

    private static Task<ComuneImportResult> ImportAsync(AppDbContext db, bool partial, params string[] rows) =>
        ImportAsync(db, Reference, partial, rows);

    private static Task<ComuneImportResult> ImportAsync(AppDbContext db, DateOnly referenceDate, bool partial, params string[] rows) =>
        ImportBytesAsync(db, Encoding.UTF8.GetBytes(OfficialHeader + "\n" + string.Join("\n", rows) + "\n"), referenceDate, partial);

    private static Task<ComuneImportResult> ImportTextAsync(AppDbContext db, string header, params string[] rows) =>
        ImportBytesAsync(db, Encoding.UTF8.GetBytes(header + "\n" + string.Join("\n", rows) + "\n"));

    private static async Task<ComuneImportResult> ImportBytesAsync(AppDbContext db, byte[] bytes, DateOnly? referenceDate = null, bool partial = false)
    {
        await using var stream = new MemoryStream(bytes);
        var result = await ComuneTestData.CreateImportService(db).ImportAsync(
            new ComuneImportRequest("test.csv", "test (synthetic rows)", referenceDate ?? Reference, "test", IsPartial: partial),
            stream);
        db.ChangeTracker.Clear();
        return result;
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
