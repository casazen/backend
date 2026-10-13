using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A9-35 / A1-43: the whole migration chain must apply on an empty PostgreSQL database, and the
/// model snapshot must match the current model (no change left without a migration).
/// </summary>
public class PostgresMigrationTests : IAsyncLifetime
{
    // Hand-written migrations without a .Designer.cs: they must still be discovered and applied.
    private const string NormalizeOrgPublicHostState = "20260725110112_NormalizeOrgPublicHostState";
    private const string RestrictCustomDomainUniqueness = "20260730111500_RestrictCustomDomainUniquenessToVerified";

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migrate_OnEmptyDatabase_AppliesEveryMigration()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());

        await db.Database.MigrateAsync();

        var expected = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, applied);
        Assert.Contains(NormalizeOrgPublicHostState, applied);
        Assert.Contains(RestrictCustomDomainUniqueness, applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task HasPendingModelChanges_AfterApplyingAllMigrations_ReturnsFalse()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model differs from AppDbContextModelSnapshot: add a migration with " +
            "`dotnet ef migrations add <Name> --project Casazen.Infrastructure --startup-project Casazen.Web`.");
    }

    // Pending-model-changes is asserted explicitly above, so Migrate() must not throw on it first.
    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_database!.ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
}
