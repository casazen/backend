using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>SU-04: the comuni of the SEO pages come from the official ISTAT list; the pilot is only names and provinces.</summary>
public class SeoComuneCatalogTests
{
    [Fact]
    public async Task GetPilotsAsync_ListNotImported_IsEmpty()
    {
        await using var db = CreateDb();

        Assert.Empty(await Catalog(db).GetPilotsAsync());
        Assert.Null(await Catalog(db).GetByCodeAsync("058091"));
    }

    [Fact]
    public async Task GetPilotsAsync_TakesCodeRegionAndSlugsFromTheList()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        var pilots = (await Catalog(db).GetPilotsAsync()).ToDictionary(c => c.ComuneSlug);

        Assert.Equal(SeoPilotComuni.All.Count, pilots.Count);
        Assert.Equal(("001272", "PIE", "piemonte"), (pilots["torino"].Code, pilots["torino"].RegionCode, pilots["torino"].RegionSlug));
        Assert.Equal("013250", pilots["bellagio"].Code);
        Assert.Equal("013145", pilots["menaggio"].Code);
        Assert.Equal(("097084", "LOM", "lombardia"), (pilots["varenna"].Code, pilots["varenna"].RegionCode, pilots["varenna"].RegionSlug));
        Assert.Equal(("058091", "LAZ", "lazio"), (pilots["roma"].Code, pilots["roma"].RegionCode, pilots["roma"].RegionSlug));
        Assert.Equal("emilia-romagna", pilots["bologna"].RegionSlug);
    }

    [Fact]
    public async Task GetPilotsAsync_APilotMissingFromTheList_IsSkippedNotInvented()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        db.Comuni.Remove(await db.Comuni.SingleAsync(c => c.IstatCode == ComuneTestData.Palermo));
        await db.SaveChangesAsync();

        var pilots = await Catalog(db).GetPilotsAsync();

        Assert.Equal(SeoPilotComuni.All.Count - 1, pilots.Count);
        Assert.DoesNotContain(pilots, c => c.ComuneSlug == "palermo");
    }

    [Fact]
    public async Task SlugLookups_CoverThePilotsOnly()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var catalog = Catalog(db);

        Assert.Equal("013075", (await catalog.GetPilotBySlugAsync("como"))!.Code);
        Assert.Equal("013075", (await catalog.GetPilotByRegionAndComuneSlugAsync("lombardia", "COMO"))!.Code);
        Assert.Null(await catalog.GetPilotByRegionAndComuneSlugAsync("toscana", "como"));
        // Castro exists twice in Italy and is no pilot: a slug does not name it.
        Assert.Null(await catalog.GetPilotBySlugAsync("castro"));
        Assert.Null(await catalog.GetPilotBySlugAsync(null));
    }

    [Fact]
    public async Task ResolveSlugOrCodeAsync_AnyComuneByCode_APilotBySlug()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var catalog = Catalog(db);

        Assert.Equal("001272", (await catalog.ResolveSlugOrCodeAsync("torino"))!.Code);
        Assert.Equal("Genova", (await catalog.ResolveSlugOrCodeAsync("010025"))!.Name);
        Assert.Equal("108019", (await catalog.ResolveSlugOrCodeAsync("108019"))!.Code);
        Assert.Null(await catalog.ResolveSlugOrCodeAsync("999999"));
        Assert.Null(await catalog.ResolveSlugOrCodeAsync("nowhere"));
    }

    [Fact]
    public async Task GetByCodesAsync_OnlyActiveComuni()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        (await db.Comuni.SingleAsync(c => c.IstatCode == "058091")).IsActive = false;
        await db.SaveChangesAsync();

        var found = await Catalog(db).GetByCodesAsync(["058091", "015146", "999999"]);

        Assert.Equal(["015146"], found.Keys);
    }

    [Fact]
    public void SeoPilotComuni_AreNamesAndProvincesNotCodes()
    {
        Assert.Equal(12, SeoPilotComuni.All.Count);
        Assert.All(SeoPilotComuni.All, pilot =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pilot.Name));
            Assert.True(ComuneRules.IsProvinceCode(pilot.ProvinceCode));
        });
        Assert.Contains(("Varenna", "LC"), SeoPilotComuni.All);
    }

    private static SeoComuneCatalog Catalog(AppDbContext db) => new(new ComuneDirectory(db));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
