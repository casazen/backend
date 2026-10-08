using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SU-04 (A4-12): which suppliers operate in a comune. By ISTAT code when the comune is known; the written name only as a
/// fallback. Rows of the official sample; the suppliers are synthetic.
/// </summary>
public class SupplierComuneMatcherTests
{
    [Fact]
    public async Task FilterAsync_SupplierChoseTheComuneFromTheList_MatchesThePropertyByCode()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var milano = Supplier(chosen: [ComuneTestData.Milano]);
        var firenze = Supplier(chosen: [ComuneTestData.Firenze]);

        var found = await Matcher(db).FilterAsync([milano, firenze], new ComuneTarget(ComuneTestData.Milano, "Milano (MI)"));

        Assert.Equal([milano.OrgId], found.Select(s => s.OrgId));
    }

    [Fact]
    public async Task FilterAsync_OldCadastralCodeF205_IsMilanoNotFirenze()
    {
        // The audit case: an invite with comune=F205 for a supplier of Milano showed it to the hosts of Firenze.
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var supplier = Supplier(written: ["F205"]);
        var matcher = Matcher(db);

        Assert.Single(await matcher.FilterAsync([supplier], new ComuneTarget(ComuneTestData.Milano, "Milano")));
        Assert.Single(await matcher.FilterAsync([supplier], new ComuneTarget(null, "Milano")));
        Assert.Empty(await matcher.FilterAsync([supplier], new ComuneTarget(ComuneTestData.Firenze, "Firenze")));
        Assert.Empty(await matcher.FilterAsync([supplier], new ComuneTarget(null, "Firenze")));
    }

    [Theory]
    [InlineData("Roma", "058091", "Roma")]
    [InlineData("H501", "058091", "Roma")]
    [InlineData("058091", "058091", "Roma")]
    [InlineData("roma", null, "ROMA")]
    [InlineData("H501", null, "Roma")]
    [InlineData("058091", null, "Roma")]
    [InlineData("Roma", "058091", "Roma Capitale")]
    public async Task FilterAsync_WrittenEntries_AreResolvedAgainstTheList(string written, string? targetCode, string targetCity)
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var found = await Matcher(db).FilterAsync([Supplier(written: [written])], new ComuneTarget(targetCode, targetCity));

        Assert.Single(found);
    }

    [Fact]
    public async Task FilterAsync_TheCodeOfAnotherComune_IsNotAMatch()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        // 010025 is Genova; Torino is 001272 (the old registry said Torino for 010025).
        var torino = Supplier(chosen: [ComuneTestData.Torino]);
        var genova = Supplier(chosen: [ComuneTestData.Genova]);

        var found = await Matcher(db).FilterAsync([torino, genova], new ComuneTarget(ComuneTestData.Torino, "Torino"));

        Assert.Equal([torino.OrgId], found.Select(s => s.OrgId));
    }

    [Fact]
    public async Task FilterAsync_PropertyCityWithAProvinceSuffix_MatchesByTheChosenCode()
    {
        // The audit case (b): the supplier writes "Cesano Maderno", the property's city is "Cesano Maderno (MB)".
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var supplier = Supplier(written: ["Cesano Maderno"]);
        var chosen = Supplier(chosen: ["108019"]);

        var found = await Matcher(db).FilterAsync([supplier, chosen], new ComuneTarget("108019", "Cesano Maderno (MB)"));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task FilterAsync_TwoComuniWithTheSameName_AreToldApartByCode()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var inBergamo = Supplier(chosen: [ComuneTestData.CastroBergamo]);
        var inLecce = Supplier(chosen: [ComuneTestData.CastroLecce]);

        var found = await Matcher(db).FilterAsync([inBergamo, inLecce], new ComuneTarget(ComuneTestData.CastroLecce, "Castro"));

        Assert.Equal([inLecce.OrgId], found.Select(s => s.OrgId));
    }

    [Fact]
    public async Task FilterAsync_AmbiguousWrittenName_FallsBackToTheNameAsBefore()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var supplier = Supplier(written: ["Castro"]);

        // Not resolvable: the written name equals the written city, as it did before the list.
        Assert.Single(await Matcher(db).FilterAsync([supplier], new ComuneTarget(null, "castro")));
        Assert.Empty(await Matcher(db).FilterAsync([supplier], new ComuneTarget(null, "Roma")));
    }

    [Fact]
    public async Task FilterAsync_ListNotImported_ComparesTheWrittenNamesOnly()
    {
        await using var db = CreateDb();
        var supplier = Supplier(written: ["Roma"]);
        var matcher = Matcher(db);

        Assert.Single(await matcher.FilterAsync([supplier], new ComuneTarget(null, "ROMA")));
        // Nothing can say that H501 is Roma without the list.
        Assert.Empty(await matcher.FilterAsync([Supplier(written: ["H501"])], new ComuneTarget(null, "Roma")));
        Assert.Empty(await matcher.FilterAsync([supplier], new ComuneTarget(null, "Milano")));
    }

    [Fact]
    public async Task FilterAsync_EmptyTarget_MatchesNobody()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        Assert.Empty(await Matcher(db).FilterAsync([Supplier(written: ["Roma"])], new ComuneTarget(null, "  ")));
        Assert.Empty(await Matcher(db).FilterAsync([], new ComuneTarget(ComuneTestData.Roma, null)));
    }

    [Fact]
    public async Task CoversAsync_ASupplierWithNothing_CoversNoComune()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        Assert.False(await Matcher(db).CoversAsync(Supplier(), new ComuneTarget(ComuneTestData.Roma, "Roma")));
    }

    [Theory]
    [InlineData("058091", "058091")]
    [InlineData("Roma", null)]
    [InlineData(" 058091 ", "058091")]
    public void ComuneTarget_FromInput_SixDigitsAreACode_AnythingElseAName(string input, string? expectedCode)
    {
        var target = ComuneTarget.FromInput(input);

        Assert.Equal(expectedCode, target.IstatCode);
        Assert.Equal(expectedCode is null ? input.Trim() : null, target.Name);
    }

    private static SupplierComuneMatcher Matcher(AppDbContext db) => new(new ComuneDirectory(db));

    private static SupplierProfile Supplier(string[]? chosen = null, string[]? written = null) => new()
    {
        OrgId = Guid.NewGuid(),
        Status = SupplierStatus.Active,
        LegalName = "Fornitore di prova",
        Phone = "+390000000",
        Email = $"{Guid.NewGuid():N}@example.test",
        ComuneIstatCodesJson = System.Text.Json.JsonSerializer.Serialize(chosen ?? []),
        ComuniJson = System.Text.Json.JsonSerializer.Serialize(written ?? []),
    };

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
