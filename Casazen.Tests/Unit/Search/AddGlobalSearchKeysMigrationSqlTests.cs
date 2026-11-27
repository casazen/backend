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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// UI-13a: the proof that the model and the queries say the same thing (no database needed): the generated key of each
/// searchable table, its full-text index, and the expression that index has, which the query must repeat letter by letter or the
/// planner does not use the index. The tests that re-read the script of the <c>AddGlobalSearchKeys</c> migration went away once it
/// reached production; the statements run for real in <c>GlobalSearchPostgresTests</c> (CI).
/// </summary>
public class AddGlobalSearchKeysMigrationSqlTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

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
            var named = columns.Matches(keySql).Select(match => match.Groups[1].Value).Distinct().ToList();

            Assert.NotEmpty(named);
            Assert.All(named, column => Assert.NotNull(entity.FindProperty(column)));
        }
    }

    private static string Expression(string sql) =>
        Regex.Replace(sql, @"\b[a-z][a-z0-9]*\.""SearchKey""", "\"SearchKey\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .ToLowerInvariant();

    [Fact]
    public async Task Queries_RepeatTheExpressionOfTheIndex_SoThePlannerUsesIt()
    {
        var indexExpression = "to_tsvector('simple', coalesce(\"searchkey\", ''))";

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

        Assert.Contains(statements, statement => statement.Contains("b.\"BookingCode\" LIKE @pattern", StringComparison.Ordinal));
    }
}
