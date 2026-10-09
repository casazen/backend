using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class AddComuneOfficialProfilesMigrationSqlTests
{
    [Fact]
    public void Script_CreatesVersionedProfileTables()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("_AddComuneOfficialProfiles", StringComparison.Ordinal));
        Assert.True(index > 0);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]);

        Assert.Contains("CREATE TABLE \"ComuneOfficialProfiles\"", script);
        Assert.Contains("CREATE TABLE \"ComuneOfficialProfileVersions\"", script);
        Assert.Contains("IX_ComuneOfficialProfileVersions_IstatCode_Current", script);
        Assert.Contains("IX_ComuneOfficialProfileVersions_IstatCode_VersionNumber", script);
        Assert.Contains("jsonb", script);
        Assert.DoesNotContain("DROP TABLE \"TouristTaxRates\"", script);
    }
}
