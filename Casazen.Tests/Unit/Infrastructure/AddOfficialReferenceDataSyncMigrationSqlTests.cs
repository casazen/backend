using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class AddOfficialReferenceDataSyncMigrationSqlTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

    private static string Generate(bool up)
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("_AddOfficialReferenceDataSync", StringComparison.Ordinal));
        Assert.True(index > 0, "The migration AddOfficialReferenceDataSync is not in the assembly.");

        var script = up
            ? db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index])
            : db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);
        return string.Join(' ', script.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Script_CreatesTheFetchAuditAndProvenanceColumns()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE TABLE \"OfficialSourceFetches\"", script);
        Assert.Contains("\"SourceUrl\"", script);
        Assert.Contains("\"SourceAuthority\"", script);
        Assert.Contains("\"SourceRetrievedAt\"", script);
        Assert.Contains("IX_OfficialSourceFetches_Dataset_RetrievedAt", script);
    }

    [Fact]
    public void Script_Down_DropsTheFetchAudit()
    {
        var script = Generate(up: false);

        Assert.Contains("DROP TABLE \"OfficialSourceFetches\"", script);
    }
}
