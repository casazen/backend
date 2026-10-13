using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-01 (A7-01): on a real PostgreSQL database, the <c>RliHonestRegistration</c> migration turns the submissions of the
/// old stub provider (<c>RLI-STUB-{id}</c>, never filed) and reservations left Pending into Failed registrations with a
/// timeline event, moves their leases back to Signed ("to register") and from then on refuses "Registered" without a
/// stored receipt.
/// </summary>
public class RliHonestRegistrationMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task RliHonestRegistration_StubAndOrphanSubmissions_BecomeFailedAndLeasesToRegister()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var s = new Seed();
        await InsertPreviousStateAsync(db, s);

        await db.Database.MigrateAsync();

        // Stub submission: Failed "simulated_submission", lease back to Signed (3), event RegistrationFailed (6).
        Assert.Equal((3, "simulated_submission"), await RegistrationAsync(db, s.StubRegistration));
        Assert.Equal(3, await ScalarAsync<int>(db, $"""SELECT "Status" FROM "LeaseContracts" WHERE "Id" = '{s.StubLease}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(db,
            $"""SELECT count(*) FROM "LeaseEvents" WHERE "LeaseContractId" = '{s.StubLease}' AND "EventType" = 6 AND "Payload" = 'simulated_submission'"""));

        // Reservation left Pending by an exception (lease still Signed): Failed "provider_outcome_unknown".
        Assert.Equal((3, "provider_outcome_unknown"), await RegistrationAsync(db, s.OrphanRegistration));
        Assert.Equal(3, await ScalarAsync<int>(db, $"""SELECT "Status" FROM "LeaseContracts" WHERE "Id" = '{s.OrphanLease}'"""));

        // A lease untouched by the RLI flow keeps its status.
        Assert.Equal(0, await ScalarAsync<int>(db, $"""SELECT "Status" FROM "LeaseContracts" WHERE "Id" = '{s.DraftLease}'"""));

        // From now on Registered (2) needs a stored receipt.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"""UPDATE "LeaseRegistrations" SET "Status" = 2 WHERE "Id" = {s.StubRegistration}"""));
        Assert.Equal("CK_LeaseRegistrations_RegisteredRequiresReceipt", ex.ConstraintName);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private sealed class Seed
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Property { get; } = Guid.NewGuid();
        public Guid StubLease { get; } = Guid.NewGuid();
        public Guid OrphanLease { get; } = Guid.NewGuid();
        public Guid DraftLease { get; } = Guid.NewGuid();
        public Guid StubRegistration { get; } = Guid.NewGuid();
        public Guid OrphanRegistration { get; } = Guid.NewGuid();
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_RliHonestRegistration", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    /// <summary>The state the old stub left: lease SentToProvider (5) with a SentToProvider (1) "RLI-STUB-…" registration.</summary>
    private static async Task InsertPreviousStateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Org}, 'Org LT-01', {"lt01-" + s.Org.ToString("N")}, 0, 'Org LT-01', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Property}, 'auth0|lt01', {s.Org}, 'Casa', 'Casa LT-01', 'Via Roma 1', 'Seveso', '20822', 0, 0, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "LeaseContracts" (
                "Id", "PropertyId", "OrgId", "Status", "FiscalRegime", "StartDate", "EndDate", "MonthlyRent",
                "RegistrationDeadline", "SignedPdfStoragePath", "ErasureRequested", "DataRetentionUntil", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.StubLease},   {s.Property}, {s.Org}, 5, 0, '2026-09-01', '2030-08-31', 800, '2026-10-01', '/signed/a.pdf', false, '2036-09-01', now(), now()),
              ({s.OrphanLease}, {s.Property}, {s.Org}, 3, 0, '2026-09-01', '2030-08-31', 800, '2026-10-01', '/signed/b.pdf', false, '2036-09-01', now(), now()),
              ({s.DraftLease},  {s.Property}, {s.Org}, 0, 0, '2026-09-01', '2030-08-31', 800, '2026-10-01', NULL, false, '2036-09-01', now(), now());

            INSERT INTO "LeaseRegistrations" ("Id", "LeaseContractId", "Status", "ExternalRegistrationId", "RegistrationCode", "SubmittedAt")
            VALUES
              ({s.StubRegistration},   {s.StubLease},   1, {"RLI-STUB-" + s.StubLease.ToString("N")}, NULL, now()),
              ({s.OrphanRegistration}, {s.OrphanLease}, 0, NULL, NULL, NULL);
            """);
    }

    /// <summary>(Status, FailureCode) of a registration.</summary>
    private static async Task<(int Status, string? FailureCode)> RegistrationAsync(AppDbContext db, Guid id)
    {
        await using var command = await CommandAsync(db,
            $"""SELECT "Status", "FailureCode" FROM "LeaseRegistrations" WHERE "Id" = '{id}'""");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    // Test-only SQL built from GUIDs generated in this class, never from input.
    private static async Task<T> ScalarAsync<T>(AppDbContext db, string sql)
    {
        await using var command = await CommandAsync(db, sql);
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)value;
    }

    private static async Task<System.Data.Common.DbCommand> CommandAsync(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync();

        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
}
