using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-11: on a real PostgreSQL database, the <c>AlloggiatiHonestStatus</c> migration turns the simulated
/// "Submitted" reports (no receipt) into "to send manually", keeps a report with a receipt as sent, merges duplicate
/// (booking, guest) rows, moves check-in sessions marked "Alloggiati sent" back to "complete" and from then on
/// refuses "sent" without a receipt.
/// </summary>
public class AlloggiatiHonestStatusMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AlloggiatiHonestStatus_SimulatedReports_BecomeToSendManuallyWithoutSentDate()
    {
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(PreviousMigration(db));
        var s = new Seed();
        await InsertPreviousStateAsync(db, s);

        await db.Database.MigrateAsync();

        // Submitted without receipt (the simulation): to send manually, no sent date, no "simulated" text.
        Assert.Equal((4, null, false, false), await ReportAsync(db, s.SimulatedReport));
        // Submitted with a receipt reference: sent, date kept.
        Assert.Equal((2, null, true, false), await ReportAsync(db, s.ReceiptReport));
        // Failed (validation of the simulation) and Confirmed without receipt: to send manually.
        Assert.Equal((4, null, false, false), await ReportAsync(db, s.FailedReport));
        Assert.Equal((4, null, false, false), await ReportAsync(db, s.ConfirmedReport));
        // Duplicate (booking, guest): only the latest row is kept.
        Assert.Equal(0L, await ScalarAsync<long>(db, $"""SELECT count(*) FROM "AlloggiatiWebReports" WHERE "Id" = '{s.OlderDuplicate}'"""));
        Assert.Equal((4, null, false, false), await ReportAsync(db, s.NewerDuplicate));

        // Session marked "Alloggiati sent" on queueing: back to complete; kept only when a receipt exists.
        Assert.Equal(2, await ScalarAsync<int>(db, $"""SELECT "Status" FROM "GuestCheckInSessions" WHERE "Id" = '{s.SimulatedSession}'"""));
        Assert.Equal(3, await ScalarAsync<int>(db, $"""SELECT "Status" FROM "GuestCheckInSessions" WHERE "Id" = '{s.ReceiptSession}'"""));

        // From now on "Inviato" (2) needs a receipt reference.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"""UPDATE "AlloggiatiWebReports" SET "Status" = 2 WHERE "Id" = {s.SimulatedReport}"""));
        Assert.Equal("CK_AlloggiatiWebReports_SentRequiresReceipt", ex.ConstraintName);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private sealed class Seed
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Property { get; } = Guid.NewGuid();
        public Guid[] Guests { get; } = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        public Guid[] Bookings { get; } = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        public Guid SimulatedReport { get; } = Guid.NewGuid();
        public Guid ReceiptReport { get; } = Guid.NewGuid();
        public Guid FailedReport { get; } = Guid.NewGuid();
        public Guid ConfirmedReport { get; } = Guid.NewGuid();
        public Guid OlderDuplicate { get; } = Guid.NewGuid();
        public Guid NewerDuplicate { get; } = Guid.NewGuid();
        public Guid SimulatedSession { get; } = Guid.NewGuid();
        public Guid ReceiptSession { get; } = Guid.NewGuid();
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AlloggiatiHonestStatus", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    /// <summary>The schema right before AlloggiatiHonestStatus: statuses 0 Pending, 1 Submitted, 2 Confirmed, 3 Failed.</summary>
    private static async Task InsertPreviousStateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Org}, 'Org CO-11', {"co11-" + s.Org.ToString("N")}, 0, 'Org CO-11', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Property}, 'auth0|co11', {s.Org}, 'Casa', 'Casa CO-11', 'Via Roma 1', 'Roma', '00100', 0, 0, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());
            """);

        for (var i = 0; i < s.Guests.Length; i++)
        {
            var guest = s.Guests[i];
            var booking = s.Bookings[i];
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Guests" (
                    "Id", "OrgId", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                    "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress",
                    "Notes", "ConsentVersion", "MarketingConsent", "ErasureRequested", "DataRetentionUntil",
                    "DataProcessingPurpose", "IsDeleted", "DeletionReason", "CreatedAt", "UpdatedAt")
                VALUES ({guest}, {s.Org}, 'Ospite', 'CO11', {"g" + i + "@example.com"}, '', '', '', '', '', '', '', '', '', '', '', '',
                    false, false, now() + interval '7 years', 'booking', false, '', now(), now());

                INSERT INTO "Bookings" (
                    "Id", "PropertyId", "GuestId", "OrgId", "CheckInDate", "CheckOutDate",
                    "NumberOfGuests", "Status", "Source", "ExternalId", "BasePrice", "TouristTax", "TotalPrice",
                    "TouristTaxAmount", "NumberOfAdults", "NumberOfChildren", "SpecialRequests", "CreatedAt", "UpdatedAt")
                VALUES ({booking}, {s.Property}, {guest}, {s.Org}, date_trunc('day', now()), date_trunc('day', now()) + interval '2 days',
                    1, 1, 0, '', 0, 0, 200, 0, 1, 0, '', now(), now());
                """);
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AlloggiatiWebReports" ("Id", "BookingId", "GuestId", "ReportedAt", "Status", "ConfirmationNumber", "ErrorMessage", "RetryCount", "ManuallyCompleted", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.SimulatedReport}, {s.Bookings[0]}, {s.Guests[0]}, now(), 1, NULL, 'simulated', 0, true, now(), now()),
              ({s.ReceiptReport},   {s.Bookings[1]}, {s.Guests[1]}, now(), 1, 'RIC-2026-0001', NULL, 0, false, now(), now()),
              ({s.FailedReport},    {s.Bookings[2]}, {s.Guests[2]}, now(), 3, NULL, 'Validation failed: required Alloggiati Web fields missing', 1, false, now(), now()),
              ({s.ConfirmedReport}, {s.Bookings[3]}, {s.Guests[3]}, now(), 2, NULL, NULL, 0, false, now(), now()),
              ({s.OlderDuplicate},  {s.Bookings[4]}, {s.Guests[4]}, now(), 3, NULL, 'missing data', 0, false, now() - interval '2 days', now() - interval '2 days'),
              ({s.NewerDuplicate},  {s.Bookings[4]}, {s.Guests[4]}, now(), 1, NULL, 'simulated', 0, false, now() - interval '1 day', now() - interval '1 day');

            INSERT INTO "GuestCheckInSessions" ("Id", "BookingId", "OrgId", "TokenHash", "ExpiresAt", "Status", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.SimulatedSession}, {s.Bookings[0]}, {s.Org}, {s.SimulatedSession.ToString("N")}, now() + interval '7 days', 3, now(), now()),
              ({s.ReceiptSession},   {s.Bookings[1]}, {s.Org}, {s.ReceiptSession.ToString("N")}, now() + interval '7 days', 3, now(), now());
            """);
    }

    /// <summary>(Status, ErrorMessage, has ReportedAt, ManuallyCompleted) of a report.</summary>
    private static async Task<(int Status, string? Error, bool HasReportedAt, bool Manual)> ReportAsync(AppDbContext db, Guid id)
    {
        await using var command = await CommandAsync(db, $"""
            SELECT "Status", "ErrorMessage", "ReportedAt" IS NOT NULL, "ManuallyCompleted"
            FROM "AlloggiatiWebReports" WHERE "Id" = '{id}'
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3));
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
