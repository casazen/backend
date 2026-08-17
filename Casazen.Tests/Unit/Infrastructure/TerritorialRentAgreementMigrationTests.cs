using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class TerritorialRentAgreementMigrationTests
{
    [Fact]
    public void AddTerritorialRentAgreements_IsLastAndCreatesReferenceTables()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        Assert.EndsWith("AddTerritorialRentAgreements", keys[^1]);

        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: keys[^2],
            toMigration: keys[^1]);

        Assert.Contains("CREATE TABLE \"TerritorialRentAgreements\"", script);
        Assert.Contains("CREATE TABLE \"ConcordatoRentBands\"", script);
        Assert.Contains("CREATE TABLE \"TerritorialAgreementSignatories\"", script);
        Assert.Contains("CREATE TABLE \"HighTensionAreaComuni\"", script);
        Assert.Contains("INSERT INTO \"TerritorialRentAgreements\"", script);
        Assert.Contains("Seveso", script);
        Assert.Contains("Cesano Maderno", script);
        Assert.Contains("Monza", script);
    }

    [Fact]
    public void MbSeed_MissingComuni_HaveNoBands()
    {
        var missing = CanoneConcordatoMbSeed.BuildAgreements()
            .Where(a => a.DataCompleteness == DataCompleteness.Missing)
            .ToList();

        Assert.Equal(52, missing.Count);
        Assert.All(missing, a => Assert.Empty(a.Bands));
        Assert.Equal(54, CanoneConcordatoMbSeed.ProvinceComuni.Length);
        Assert.All(
            CanoneConcordatoMbSeed.BuildAgreements().Where(a => CanoneConcordatoMbSeed.PilotComuni.Contains(a.Comune)),
            a => Assert.NotEmpty(a.Bands));
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
}
