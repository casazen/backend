using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Search;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Search;
using Casazen.Tests.Unit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// UI-13a: the migration <c>AddGlobalSearchKeys</c> as the Npgsql provider generates it (no database needed, the approach of
/// <c>MigrationSqlTests</c>), and the proof that the model, the migration and the queries say the same thing: the generated key of
/// each searchable table, its full-text index, and the expression that index has, which the query must repeat letter by letter or
/// the planner does not use the index. The statements run for real in <c>AddGlobalSearchKeysMigrationPostgresTests</c> and
/// <c>GlobalSearchPostgresTests</c> (CI).
/// </summary>
public class AddGlobalSearchKeysMigrationSqlTests
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
        var index = keys.FindIndex(k => k.EndsWith("_AddGlobalSearchKeys", StringComparison.Ordinal));
        Assert.True(index > 0, "The migration AddGlobalSearchKeys is not in the assembly.");

        var migrator = db.GetService<IMigrator>();
        var script = up
            ? migrator.GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index])
            : migrator.GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);
        return Normalize(script);
    }

    /// <summary>The script on one line, runs of whitespace collapsed: the assertions do not depend on the line breaks.</summary>
    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    public static TheoryData<string, string, string> Tables()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (_, table, keySql, indexName) in SearchKeyModel.Keys)
            data.Add(table, keySql, indexName);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Script_AddsTheGeneratedKeyOfTheTable_StoredAndComputedByTheDatabase(string table, string keySql, string indexName)
    {
        var script = Generate(up: true);

        Assert.Contains($"ALTER TABLE \"{table}\" ADD \"SearchKey\" text GENERATED ALWAYS AS ({keySql}) STORED;", script);
        Assert.EndsWith("_Fts", indexName, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Script_IndexesTheKeyWithAFullTextIndexOfTheSimpleConfiguration_NoExtension(string table, string keySql, string indexName)
    {
        var script = Generate(up: true);

        Assert.NotEmpty(keySql);
        Assert.Contains(
            $"CREATE INDEX \"{indexName}\" ON \"{table}\" USING gin (to_tsvector('simple', coalesce(\"SearchKey\", '')));",
            script);
        // pg_trgm and unaccent are not used: the environments share one database, one schema each (docs/runbooks/global-search.md).
        Assert.DoesNotContain("EXTENSION", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gin_trgm_ops", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unaccent(", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_IndexesThePrefixOfTheBookingCode_WithThePatternOperatorClass()
    {
        Assert.Contains(
            "CREATE INDEX \"IX_Bookings_BookingCode_Prefix\" ON \"Bookings\" (\"BookingCode\" varchar_pattern_ops);",
            Generate(up: true));
    }

    [Fact]
    public void Script_ChangesNothingElse_NoDataIsWrittenAndNoColumnDropped()
    {
        var script = Generate(up: true);

        Assert.DoesNotContain("UPDATE ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE ", script, StringComparison.Ordinal);
        // The only row written is the one of the history of the migrations.
        Assert.DoesNotMatch("INSERT INTO \"(?!__EFMigrationsHistory)", script);
        Assert.Equal(SearchKeyModel.Keys.Count, Regex.Matches(script, "ALTER TABLE ").Count);
        Assert.Equal(SearchKeyModel.Keys.Count + 1, Regex.Matches(script, "CREATE INDEX ").Count);
    }

    [Fact]
    public void Down_DropsTheIndexesAndTheColumns_AndNothingElse()
    {
        var down = Generate(up: false);

        foreach (var (_, table, _, indexName) in SearchKeyModel.Keys)
        {
            Assert.Contains($"DROP INDEX \"{indexName}\";", down);
            Assert.Contains($"ALTER TABLE \"{table}\" DROP COLUMN \"SearchKey\";", down);
        }

        Assert.Contains("DROP INDEX \"IX_Bookings_BookingCode_Prefix\";", down);
        Assert.DoesNotContain("DROP TABLE", down, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_EverySearchableTable_HasTheGeneratedKeyAndTheIndexTheMigrationCreates()
    {
        using var db = NewNpgsqlContext();
        var model = db.GetService<IDesignTimeModel>().Model;

        var withAKey = model.GetEntityTypes()
            .Where(entity => entity.FindProperty(SearchKeyModel.KeyProperty) is not null)
            .Select(entity => entity.ClrType)
            .OrderBy(type => type.Name)
            .ToList();
        Assert.Equal(SearchKeyModel.Keys.Select(key => key.Entity).OrderBy(type => type.Name).ToList(), withAKey);

        foreach (var (entityType, table, keySql, indexName) in SearchKeyModel.Keys)
        {
            var entity = model.FindEntityType(entityType)!;
            var key = entity.FindProperty(SearchKeyModel.KeyProperty)!;

            Assert.True(key.IsShadowProperty());
            Assert.Equal(typeof(string), key.ClrType);
            Assert.Equal("text", key.GetColumnType());
            Assert.Equal(keySql, key.GetComputedColumnSql());
            Assert.True(key.GetIsStored());
            Assert.Equal(ValueGenerated.OnAddOrUpdate, key.ValueGenerated);
            Assert.Equal(table, entity.GetTableName());

            var index = Assert.Single(entity.GetIndexes(), i => i.Properties.Any(p => p.Name == SearchKeyModel.KeyProperty));
            Assert.Equal(indexName, index.GetDatabaseName());
            Assert.Equal("gin", index.GetMethod());
            Assert.Equal("simple", index.GetTsVectorConfig());
        }
    }

    [Fact]
    public void Model_TheKeyAreBuiltFromTheColumnsTheEntityHas()
    {
        using var db = NewNpgsqlContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var columns = new Regex("\"([A-Za-z]+)\"");

        foreach (var (entityType, _, keySql, _) in SearchKeyModel.Keys)
        {
            var entity = model.FindEntityType(entityType)!;
            // Only the part before the translate lists names columns: the folded expression of the SQL has no other quoted identifier.
            var named = columns.Matches(keySql).Select(match => match.Groups[1].Value).Distinct().ToList();

            Assert.NotEmpty(named);
            Assert.All(named, column => Assert.NotNull(entity.FindProperty(column)));
        }
    }

    // ─── The query repeats the expression of the index ──────────────────────────────────────────────────

    /// <summary>The full-text expression of a statement, without the alias of the table and with the case of the keywords flattened.</summary>
    private static string Expression(string sql) =>
        Regex.Replace(sql, @"\b[a-z][a-z0-9]*\.""SearchKey""", "\"SearchKey\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .ToLowerInvariant();

    [Fact]
    public async Task Queries_RepeatTheExpressionOfTheIndex_SoThePlannerUsesIt()
    {
        var indexExpression = "to_tsvector('simple', coalesce(\"searchkey\", ''))";
        var created = Generate(up: true).ToLowerInvariant();
        Assert.Contains($"using gin ({indexExpression})", created);

        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var scope = new HostScope(Guid.NewGuid());
        var request = new GlobalSearchRequest(
            SearchText.Parse("rossi mar"),
            5,
            new HostSearchAccess(scope, true, true, true, true, true),
            Guid.NewGuid(),
            CultureInfo.GetCultureInfo("it-IT"));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => new GlobalSearchService(db, NullLogger<GlobalSearchService>.Instance).SearchAsync(request));

        // properties, bookings by guest, guests, leases (tenants), host requests, suppliers, the inbox of the supplier
        var withText = statements.Where(statement => statement.Contains("to_tsvector", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(withText.Count >= 7, $"{withText.Count} statements with a full-text predicate");
        foreach (var statement in withText)
        {
            Assert.Contains(indexExpression, Expression(statement));
            Assert.Contains("@@ to_tsquery('simple', @tsquery)", Expression(statement));
        }
    }

    [Fact]
    public async Task Queries_TheBookingCodeIsAPrefixOfTheColumnTheIndexCovers()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var request = new GlobalSearchRequest(
            SearchText.Parse("7K3M9-PQ2XV"),
            5,
            new HostSearchAccess(new HostScope(Guid.NewGuid()), false, false, true, false, false),
            null,
            CultureInfo.InvariantCulture);
        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => new GlobalSearchService(db, NullLogger<GlobalSearchService>.Instance).SearchAsync(request));

        // A LIKE on the stored column, with the pattern as a parameter: an index range for the planner (varchar_pattern_ops).
        Assert.Contains(statements, statement => statement.Contains("b.\"BookingCode\" LIKE @pattern", StringComparison.Ordinal));
    }
}
