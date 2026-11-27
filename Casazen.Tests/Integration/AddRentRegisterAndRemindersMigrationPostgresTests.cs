using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-01 (B1) on real PostgreSQL, on the schema as it is now (a copy of the migrated template): the migration
/// <c>AddRentRegisterAndReminders</c> added the index of the rent register <c>(OrgId, DueDate)</c> to <c>RentLedgerEntries</c> and
/// kept the older indexes. What it did to the installments that existed (they keep every value and get "never reminded"), the
/// defaults it gave to a writer that does not know the reminder columns, and its revert ran when the release that carried it went
/// to production: they are not replayed any more.
/// </summary>
public class AddRentRegisterAndRemindersMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_CreatesTheIndexOfTheRegister_AndKeepsTheOlderOneOfTheOrg()
    {
        await using var db = _database!.CreateContext();

        var indexes = await IndexDefinitionsAsync(db);
        var register = Assert.Single(indexes, i => i.Name == "IX_RentLedgerEntries_OrgId_DueDate");
        // The org and then the due date, in that order: the month of an org is one range of the index, already in due date order.
        Assert.Contains("(\"OrgId\", \"DueDate\")", register.Definition, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE", register.Definition, StringComparison.Ordinal);
        Assert.Contains(indexes, i => i.Name == "IX_RentLedgerEntries_OrgId");
        Assert.Contains(indexes, i => i.Name == "IX_RentLedgerEntries_LeaseContractId_PeriodStart");
    }

    private sealed record IndexRow(string Name, string Definition);

    private static async Task<List<IndexRow>> IndexDefinitionsAsync(AppDbContext db)
    {
        var rows = await db.Database
            .SqlQuery<string>($"""SELECT indexname::text || '|' || indexdef AS "Value" FROM pg_indexes WHERE tablename = 'RentLedgerEntries'""")
            .ToListAsync();
        return rows.Select(r => r.Split('|', 2)).Select(p => new IndexRow(p[0], p[1])).ToList();
    }
}
