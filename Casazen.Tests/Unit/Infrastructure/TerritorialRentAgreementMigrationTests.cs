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
        var territorial = keys.Single(k => k.EndsWith("AddTerritorialRentAgreements", StringComparison.Ordinal));
        var idx = keys.IndexOf(territorial);
        Assert.True(idx > 0);

        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: keys[idx - 1],
            toMigration: territorial);

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

        Assert.Equal(53, missing.Count);
        Assert.All(missing, a => Assert.Empty(a.Bands));
        Assert.Equal(55, CanoneConcordatoMbSeed.ProvinceComuni.Length);
        Assert.All(
            CanoneConcordatoMbSeed.BuildAgreements().Where(a => CanoneConcordatoMbSeed.PilotComuni.Contains(a.Comune)),
            a => Assert.NotEmpty(a.Bands));
    }

    [Fact]
    public void AddTerritorialRentAgreements_UsesTheFrozen2024Seed()
    {
        // A7-22: the old migration must not change when the reference data do (LT-10 added Misinto and signatories).
        Assert.Equal(54, CanoneConcordatoMbSeed2024.ProvinceComuni.Length);
        Assert.DoesNotContain("Misinto", CanoneConcordatoMbSeed2024.ProvinceComuni);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Casazen.Infrastructure", "Migrations", "20260816203709_AddTerritorialRentAgreements.cs"));
        var source = File.ReadAllText(path);
        Assert.Contains("CanoneConcordatoMbSeed2024.BuildAgreements()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CanoneConcordatoMbSeed.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseContractTypeAndConcordatoRules_BackfillsLeasesAndAppliesTheMbCorrections()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var migration = keys.Single(k => k.EndsWith("LeaseContractTypeAndConcordatoRules", StringComparison.Ordinal));

        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: keys[keys.IndexOf(migration) - 1], toMigration: migration);

        Assert.Contains("SET \"ContractType\" = CASE \"FiscalRegime\" WHEN 2 THEN 1 ELSE 0 END", script, StringComparison.Ordinal);
        Assert.Contains("\"MinSqm\" IN (51, 75, 100)", script, StringComparison.Ordinal);
        Assert.Contains("Misinto", script, StringComparison.Ordinal);
        Assert.Contains("UNIONCASA", script, StringComparison.Ordinal);
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
}
