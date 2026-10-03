using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>SU-04: search, validation and resolution on the official ISTAT list (rows of the official sample).</summary>
public class ComuneDirectoryTests
{
    [Fact]
    public async Task GetStatus_BeforeTheImport_SaysTheListIsNotThere()
    {
        await using var db = CreateDb();
        var directory = new ComuneDirectory(db);

        var status = await directory.GetStatusAsync();

        Assert.False(status.Available);
        Assert.Equal((0, 0), (status.TotalRows, status.ActiveRows));
        Assert.Null(status.LastImport);
        Assert.False(await directory.IsAvailableAsync());
        Assert.Empty(await directory.SearchAsync("Roma", 10));
    }

    [Fact]
    public async Task GetStatus_AfterTheImport_NamesTheSourceAndTheReferenceDate()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var status = await new ComuneDirectory(db).GetStatusAsync();

        Assert.True(status.Available);
        Assert.Equal((29, 29), (status.TotalRows, status.ActiveRows));
        Assert.Equal((ComuneTestData.ReferenceDate, ComuneTestData.SampleSourceVersion, 29), (status.LastImport!.ReferenceDate, status.LastImport.SourceVersion, status.LastImport.RowCount));
    }

    [Theory]
    [InlineData("mila", "015146")]
    [InlineData("MILANO", "015146")]
    [InlineData("forli", "040012")]
    [InlineData("Forlì", "040012")]
    [InlineData("reggio nell emilia", "035033")]
    [InlineData("Reggio nell'Emilia", "035033")]
    [InlineData("sant agata", "062070")]
    [InlineData("bozen", "021008")]
    [InlineData("Bolzano", "021008")]
    [InlineData("058091", "058091")]
    [InlineData("H501", "058091")]
    [InlineData("h501", "058091")]
    public async Task SearchAsync_FindsTheComune(string query, string expectedIstat)
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var found = await new ComuneDirectory(db).SearchAsync(query, 10);

        Assert.Contains(found, c => c.IstatCode == expectedIstat);
    }

    [Fact]
    public async Task SearchAsync_ExactNameFirstThenTheShortest()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        // The exact name leads, whatever else contains or starts with the letters.
        var found = await new ComuneDirectory(db).SearchAsync("como", 10);

        Assert.Equal("013075", found[0].IstatCode);
    }

    [Fact]
    public async Task SearchAsync_SameNameInTwoProvinces_ReturnsBoth()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var found = await new ComuneDirectory(db).SearchAsync("Castro", 10);

        Assert.Equal(["BG", "LE"], found.Select(c => c.ProvinceCode).Order());
    }

    [Fact]
    public async Task SearchAsync_RespectsTheLimitAndNeverReturnsInactiveComuni()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var castro = await db.Comuni.SingleAsync(c => c.IstatCode == ComuneTestData.CastroLecce);
        castro.IsActive = false;
        await db.SaveChangesAsync();
        var directory = new ComuneDirectory(db);

        Assert.Single(await directory.SearchAsync("Castro", 10));
        Assert.Single(await directory.SearchAsync("o", 1));
        Assert.Empty(await directory.SearchAsync("  ", 10));
        Assert.Empty(await directory.SearchAsync("zzzz", 10));
    }

    [Fact]
    public async Task FindByIstatCodeAsync_ActiveOnlyByDefault_AndTheCodeIsAString()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var directory = new ComuneDirectory(db);

        Assert.Equal("Torino", (await directory.FindByIstatCodeAsync("001272"))!.Name);
        Assert.Equal("Torino", (await directory.FindByIstatCodeAsync(" 001272 "))!.Name);
        Assert.Null(await directory.FindByIstatCodeAsync("1272"));
        Assert.Null(await directory.FindByIstatCodeAsync("999999"));
        Assert.Null(await directory.FindByIstatCodeAsync(null));

        var torino = await db.Comuni.SingleAsync(c => c.IstatCode == "001272");
        torino.IsActive = false;
        await db.SaveChangesAsync();
        Assert.Null(await directory.FindByIstatCodeAsync("001272"));
        Assert.NotNull(await directory.FindByIstatCodeAsync("001272", activeOnly: false));
    }

    [Fact]
    public async Task GetByIstatCodesAsync_ReturnsTheKnownCodesOnly()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var found = await new ComuneDirectory(db).GetByIstatCodesAsync(["058091", "015146", "999999", "bad", "058091"]);

        Assert.Equal(["015146", "058091"], found.Keys.Order());
    }

    [Fact]
    public async Task ResolveAsync_CodesAndUniqueNames_AreResolved_AmbiguousAndUnknownAreNot()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var resolved = await new ComuneDirectory(db).ResolveAsync(["H501", "roma", "058091", "F205", "D612", "Castro", "Livo", "Nowhere", "Rome", " Firenze "]);

        Assert.Equal("058091", resolved["H501"].IstatCode);
        Assert.Equal("058091", resolved["roma"].IstatCode);
        Assert.Equal("058091", resolved["058091"].IstatCode);
        // F205 is Milano and D612 Firenze (the old registry had F205 as Firenze).
        Assert.Equal("015146", resolved["F205"].IstatCode);
        Assert.Equal("048017", resolved["D612"].IstatCode);
        Assert.Equal("048017", resolved["Firenze"].IstatCode);
        // Two comuni with the name: not guessed. Unknown and English names: not found.
        Assert.False(resolved.ContainsKey("Castro"));
        Assert.False(resolved.ContainsKey("Livo"));
        Assert.False(resolved.ContainsKey("Nowhere"));
        Assert.False(resolved.ContainsKey("Rome"));
    }

    [Fact]
    public async Task ResolveAsync_AnOldCodeOfAComuneNoLongerInTheList_StillSaysWhichOneItWas()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var roma = await db.Comuni.SingleAsync(c => c.IstatCode == "058091");
        roma.IsActive = false;
        await db.SaveChangesAsync();

        var resolved = await new ComuneDirectory(db).ResolveAsync(["058091", "H501", "Roma"]);

        Assert.Equal("058091", resolved["058091"].IstatCode);
        Assert.Equal("058091", resolved["H501"].IstatCode);
        // A name only stands for an active comune.
        Assert.False(resolved.ContainsKey("Roma"));
    }

    [Fact]
    public async Task FindByNamesAndProvincesAsync_FindsEachPairOrSkipsIt()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var found = await new ComuneDirectory(db).FindByNamesAndProvincesAsync([("Castro", "LE"), ("Varenna", "LC"), ("Varenna", "CO"), ("Nowhere", "MI")]);

        Assert.Equal(["075096", "097084"], found.Select(c => c.IstatCode).Order());
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
