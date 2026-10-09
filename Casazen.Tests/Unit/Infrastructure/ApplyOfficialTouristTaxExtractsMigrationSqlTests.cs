using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class ApplyOfficialTouristTaxExtractsMigrationSqlTests
{
    [Fact]
    public void Script_InsertsExtractedPilotRatesAndDoesNotTouchSeveso()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("_ApplyOfficialTouristTaxExtracts", StringComparison.Ordinal));
        Assert.True(index > 0);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]);

        Assert.Contains(TouristTaxOfficialExtractSeed.TorinoUrl, script);
        Assert.Contains(TouristTaxOfficialExtractSeed.BolognaUrl, script);
        Assert.Contains(TouristTaxOfficialExtractSeed.NapoliUrl, script);
        Assert.Contains("INSERT INTO \"TouristTaxRates\"", script);
        foreach (var rate in TouristTaxOfficialExtractSeed.NewRates())
        {
            Assert.True(rate.Notes.Length <= 500, $"{rate.City} notes");
            Assert.Contains(rate.Id.ToString(), script);
            Assert.False(string.IsNullOrWhiteSpace(rate.SourceUrl));
        }

        Assert.DoesNotContain("Seveso", script);
        Assert.DoesNotContain("Cesano Maderno", script);
    }
}
