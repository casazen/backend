using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-04 schema (<c>AddComuniIstat</c>) on real PostgreSQL (A4-12, A5-34, A8-24): the new columns are nullable (nothing is
/// inferred for the existing rows) and the ISTAT codes of the suppliers start as an empty list. The tests of the data that the
/// migration moved (the SEO pages and signup attributions stored with four wrong codes) went away once it reached production.
/// </summary>
public class AddComuniIstatMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_NewColumns_AreNullableAndTheSupplierCodesStartAsAnEmptyList()
    {
        await using var db = _database!.CreateContext();

        var columns = await db.Database
            .SqlQuery<ColumnInfo>($"""
                SELECT table_name AS "Table", column_name AS "Name", is_nullable AS "Nullable", column_default AS "DefaultValue"
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND ((table_name = 'Properties' AND column_name IN ('ComuneIstatCode', 'RegionCode'))
                    OR (table_name = 'SupplierProfiles' AND column_name = 'ComuneIstatCodesJson'))
                """)
            .ToListAsync();

        Assert.Equal(3, columns.Count);
        var property = columns.Where(c => c.Table == "Properties").ToList();
        Assert.All(property, c => Assert.Equal("YES", c.Nullable));
        var supplier = Assert.Single(columns, c => c.Table == "SupplierProfiles");
        Assert.Equal("NO", supplier.Nullable);
        Assert.Contains("'[]'", supplier.DefaultValue);
    }

    private sealed record ColumnInfo(string Table, string Name, string Nullable, string? DefaultValue);
}
