using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// UI-13a on a real PostgreSQL database, on the schema as it is now (a copy of the migrated template): each of the five searchable
/// tables has its <c>SearchKey</c> generated column and nowhere else does, the full-text indexes exist over the expression the queries
/// use, and so does the index of the beginning of the booking code. The keys computed for the rows that were there when the
/// <c>AddGlobalSearchKeys</c> migration ran, and its revert, ran when the release that carried it went to production: they are not
/// replayed any more (the search itself runs on PostgreSQL in <c>GlobalSearchPostgresTests</c>).
/// </summary>
public class AddGlobalSearchKeysMigrationPostgresTests : IAsyncLifetime
{
    private static readonly string[] FullTextIndexes =
    [
        "IX_Properties_SearchKey_Fts",
        "IX_Guests_SearchKey_Fts",
        "IX_Parties_SearchKey_Fts",
        "IX_ServiceRequests_SearchKey_Fts",
        "IX_SupplierProfiles_SearchKey_Fts",
    ];

    private static readonly string[] SearchableTables = ["Properties", "Guests", "Parties", "ServiceRequests", "SupplierProfiles"];

    private const string BookingPrefixIndex = "IX_Bookings_BookingCode_Prefix";

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task SearchKey_IsOneGeneratedColumnInEachSearchableTable_AndTheIndexesExist()
    {
        await using var db = _database!.CreateContext();

        var columns = await ColumnTablesAsync(db);
        Assert.Equal(SearchableTables.Order(), columns.Order());

        var definitions = await IndexDefinitionsAsync(db);
        foreach (var name in FullTextIndexes)
        {
            var definition = DefinitionOf(definitions, name);
            Assert.Contains("USING gin", definition, StringComparison.Ordinal);
            Assert.Contains("to_tsvector('simple'::regconfig", definition, StringComparison.Ordinal);
            Assert.Contains("SearchKey", definition, StringComparison.Ordinal);
        }

        var prefix = DefinitionOf(definitions, BookingPrefixIndex);
        Assert.Contains("pattern_ops", prefix, StringComparison.Ordinal);
        Assert.Contains("BookingCode", prefix, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ColumnTablesAsync(AppDbContext db) =>
        await db.Database
            .SqlQueryRaw<string>("""
                SELECT table_name::text AS "Value" FROM information_schema.columns
                WHERE column_name = 'SearchKey' AND table_schema = current_schema()
                """)
            .ToListAsync();

    private static string DefinitionOf(Dictionary<string, string> definitions, string name)
    {
        Assert.True(definitions.TryGetValue(name, out var definition), $"{name} does not exist");
        return definition!;
    }

    private static async Task<Dictionary<string, string>> IndexDefinitionsAsync(AppDbContext db)
    {
        var rows = await db.Database
            .SqlQueryRaw<string>("""
                SELECT indexname::text || '|' || indexdef AS "Value" FROM pg_indexes
                WHERE schemaname = current_schema() AND (indexname LIKE 'IX%SearchKey%Fts' OR indexname = 'IX_Bookings_BookingCode_Prefix')
                """)
            .ToListAsync();
        return rows.ToDictionary(row => row[..row.IndexOf('|')], row => row[(row.IndexOf('|') + 1)..]);
    }
}
