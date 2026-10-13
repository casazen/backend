using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-1: applies every migration up to the one before <c>AddGuestOrgIdNullable</c> on a real PostgreSQL
/// database, inserts guests shared across orgs the way the pre-TN-1 code produced them, then applies the
/// guest migrations (nullable column → split backfill → pre-flight + NOT NULL) and checks that every guest
/// ends up in exactly one org with its bookings, Alloggiati reports and check-in sessions consistent.
/// </summary>
public class GuestOrgBackfillPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task GuestMigrations_SharedGuests_SplitPerOrgAndRelinkBookingsReportsAndSessions()
    {
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var (previous, _, backfill, _) = GuestMigrationIds(db);
        migrator.Migrate(previous);

        var ids = new Seed();
        await InsertPreTenantStateAsync(db, ids);

        migrator.Migrate(backfill);
        await db.Database.MigrateAsync();

        var quarantineOrg = await ScalarAsync<Guid>(db, """SELECT "Id" FROM "Orgs" WHERE "Slug" = 'casazen-unassigned'""");

        // Owner org keeps the original row: the shared guest was first used by org A.
        Assert.Equal(ids.OrgA, await GuestOrgAsync(db, ids.SharedGuest));
        Assert.Equal(ids.SharedGuest, await BookingGuestAsync(db, ids.BookingA));

        // Both bookings of org B now point at one copy of the guest owned by org B.
        var sharedCopy = await BookingGuestAsync(db, ids.BookingB1);
        Assert.NotEqual(ids.SharedGuest, sharedCopy);
        Assert.Equal(sharedCopy, await BookingGuestAsync(db, ids.BookingB2));
        Assert.Equal(ids.OrgB, await GuestOrgAsync(db, sharedCopy));

        // The copy is a full duplicate of the shared row (data of the stays of both orgs is kept).
        var original = await GuestSnapshotAsync(db, ids.SharedGuest);
        var copy = await GuestSnapshotAsync(db, sharedCopy);
        Assert.Equal(original with { Id = copy.Id }, copy);
        Assert.Equal("AB1234567", copy.DocumentNumber);

        // The Alloggiati report of org B follows the copy; the check-in session still points at its booking.
        Assert.Equal(sharedCopy, await ScalarAsync<Guid>(db, $"""SELECT "GuestId" FROM "AlloggiatiWebReports" WHERE "Id" = '{ids.ReportB1}'"""));
        Assert.Equal(ids.BookingB1, await ScalarAsync<Guid>(db, $"""SELECT "BookingId" FROM "GuestCheckInSessions" WHERE "Id" = '{ids.SessionB1}'"""));

        // A report of org B that referenced a guest owned by org A gets its own org-B copy.
        Assert.Equal(ids.OrgA, await GuestOrgAsync(db, ids.ReportOnlyGuest));
        var reportCopy = await ScalarAsync<Guid>(db, $"""SELECT "GuestId" FROM "AlloggiatiWebReports" WHERE "Id" = '{ids.CrossReport}'""");
        Assert.NotEqual(ids.ReportOnlyGuest, reportCopy);
        Assert.NotEqual(sharedCopy, reportCopy);
        Assert.Equal(ids.OrgB, await GuestOrgAsync(db, reportCopy));

        // Single-org guest: assigned, not copied. Unused guest: quarantined, visible to no tenant.
        Assert.Equal(ids.OrgB, await GuestOrgAsync(db, ids.SingleOrgGuest));
        Assert.Equal(quarantineOrg, await GuestOrgAsync(db, ids.UnusedGuest));
        Assert.Equal(6L, await ScalarAsync<long>(db, """SELECT count(*) FROM "Guests" """));

        // Tenant invariant and schema: no cross-org reference left, OrgId NOT NULL with a Restrict FK.
        Assert.Equal(0L, await ScalarAsync<long>(db, """
            SELECT count(*) FROM "Bookings" b JOIN "Guests" g ON g."Id" = b."GuestId" WHERE g."OrgId" <> b."OrgId"
            """));
        Assert.Equal(0L, await ScalarAsync<long>(db, """
            SELECT count(*) FROM "AlloggiatiWebReports" r
            JOIN "Bookings" b ON b."Id" = r."BookingId"
            JOIN "Guests" g ON g."Id" = r."GuestId"
            WHERE g."OrgId" <> b."OrgId"
            """));
        Assert.Equal("NO", await ScalarAsync<string>(db, """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'Guests' AND column_name = 'OrgId'
            """));
        Assert.Equal("r", await ScalarAsync<string>(db, """
            SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_Guests_Orgs_OrgId'
            """));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task MakeGuestOrgIdRequired_GuestStillWithoutOrg_FailsPreFlightBeforeNotNull()
    {
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var (_, _, backfill, makeRequired) = GuestMigrationIds(db);
        migrator.Migrate(backfill);

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Guests" ("Id", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress", "Notes",
                "ConsentVersion", "MarketingConsent", "ErasureRequested", "DataRetentionUntil", "DataProcessingPurpose",
                "IsDeleted", "DeletionReason", "CreatedAt", "UpdatedAt")
            VALUES (gen_random_uuid(), 'Late', 'Guest', 'late@example.com', '', '', '', '', '', '', '', '', '', '', '',
                '', false, false, now() + interval '7 years', 'booking', false, '', now(), now());
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Task.Run(() => migrator.Migrate(makeRequired)));

        Assert.Contains("Pre-flight failed", ex.MessageText);
        Assert.DoesNotContain(makeRequired, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal("YES", await ScalarAsync<string>(db, """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'Guests' AND column_name = 'OrgId'
            """));
    }

    private sealed class Seed
    {
        public Guid OrgA { get; } = Guid.NewGuid();
        public Guid OrgB { get; } = Guid.NewGuid();
        public Guid PropertyA { get; } = Guid.NewGuid();
        public Guid PropertyB { get; } = Guid.NewGuid();
        public Guid SharedGuest { get; } = Guid.NewGuid();
        public Guid SingleOrgGuest { get; } = Guid.NewGuid();
        public Guid UnusedGuest { get; } = Guid.NewGuid();
        public Guid ReportOnlyGuest { get; } = Guid.NewGuid();
        public Guid BookingA { get; } = Guid.NewGuid();
        public Guid BookingA2 { get; } = Guid.NewGuid();
        public Guid BookingB1 { get; } = Guid.NewGuid();
        public Guid BookingB2 { get; } = Guid.NewGuid();
        public Guid BookingB3 { get; } = Guid.NewGuid();
        public Guid ReportB1 { get; } = Guid.NewGuid();
        public Guid CrossReport { get; } = Guid.NewGuid();
        public Guid SessionB1 { get; } = Guid.NewGuid();
    }

    private sealed record GuestRow(
        Guid Id,
        string FirstName,
        string LastName,
        string Email,
        DateTime? DateOfBirth,
        string DocumentNumber,
        string? DocumentScanUrl,
        string Notes,
        DateTime CreatedAt);

    /// <summary>The schema right before AddGuestOrgIdNullable: Guests has no OrgId yet.</summary>
    private static async Task InsertPreTenantStateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.OrgA}, 'Org A', {"tn1-a-" + s.OrgA.ToString("N")}, 0, 'Org A', '', true, now(), now()),
              ({s.OrgB}, 'Org B', {"tn1-b-" + s.OrgB.ToString("N")}, 0, 'Org B', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.PropertyA}, 'auth0|tn1-a', {s.OrgA}, 'A1', 'Property A', 'Via A', 'Roma', '00100', 0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now()),
              ({s.PropertyB}, 'auth0|tn1-b', {s.OrgB}, 'B1', 'Property B', 'Via B', 'Milano', '20100', 0, 0, 1, 1, 2, 120, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "Guests" (
                "Id", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress",
                "Notes", "ConsentVersion", "MarketingConsent", "ErasureRequested", "DataRetentionUntil",
                "DataProcessingPurpose", "IsDeleted", "DeletionReason", "CreatedAt", "UpdatedAt",
                "DateOfBirth", "DocumentScanUrl")
            VALUES
              ({s.SharedGuest}, 'Mario', 'Rossi', 'mario@example.com', '+39333', 'Via Roma 1', 'Roma', '00100', 'Italia',
               'Milano', 'Italiana', 'AB1234567', 'Italia', '', 'Shared note', 'v1', false, false, now() + interval '7 years',
               'booking', false, '', now() - interval '30 days', now() - interval '1 day',
               '1980-01-01T00:00:00Z', 'guest-documents/a/scan.pdf'),
              ({s.SingleOrgGuest}, 'Anna', 'Verdi', 'anna@example.com', '', '', '', '', '', '', '', '', '', '', '', '', false, false,
               now() + interval '7 years', 'booking', false, '', now(), now(), NULL, NULL),
              ({s.UnusedGuest}, 'Luca', 'Neri', 'luca@example.com', '', '', '', '', '', '', '', '', '', '', '', '', false, false,
               now() + interval '7 years', 'booking', false, '', now(), now(), NULL, NULL),
              ({s.ReportOnlyGuest}, 'Sara', 'Blu', 'sara@example.com', '', '', '', '', '', '', '', '', '', '', '', '', false, false,
               now() + interval '7 years', 'booking', false, '', now(), now(), NULL, NULL);

            INSERT INTO "Bookings" (
                "Id", "PropertyId", "GuestId", "OrgId", "CheckInDate", "CheckOutDate",
                "NumberOfGuests", "Status", "Source", "ExternalId", "BasePrice", "TouristTax", "TotalPrice",
                "TouristTaxAmount", "NumberOfAdults", "NumberOfChildren", "SpecialRequests", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.BookingA},  {s.PropertyA}, {s.SharedGuest},     {s.OrgA}, now(), now() + interval '2 days', 2, 1, 0, '', 0, 0, 200, 0, 2, 0, '', now() - interval '20 days', now()),
              ({s.BookingA2}, {s.PropertyA}, {s.ReportOnlyGuest}, {s.OrgA}, now(), now() + interval '2 days', 1, 1, 0, '', 0, 0, 100, 0, 1, 0, '', now() - interval '8 days', now()),
              ({s.BookingB1}, {s.PropertyB}, {s.SharedGuest},     {s.OrgB}, now(), now() + interval '2 days', 2, 1, 0, '', 0, 0, 240, 0, 2, 0, '', now() - interval '10 days', now()),
              ({s.BookingB2}, {s.PropertyB}, {s.SharedGuest},     {s.OrgB}, now(), now() + interval '3 days', 2, 1, 0, '', 0, 0, 360, 0, 2, 0, '', now() - interval '5 days', now()),
              ({s.BookingB3}, {s.PropertyB}, {s.SingleOrgGuest},  {s.OrgB}, now(), now() + interval '1 days', 1, 1, 0, '', 0, 0, 120, 0, 1, 0, '', now() - interval '2 days', now());

            INSERT INTO "AlloggiatiWebReports" ("Id", "BookingId", "GuestId", "ReportedAt", "Status", "RetryCount", "ManuallyCompleted", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.ReportB1},    {s.BookingB1}, {s.SharedGuest},     now(), 2, 0, false, now() - interval '9 days', now()),
              ({s.CrossReport}, {s.BookingB3}, {s.ReportOnlyGuest}, now(), 0, 0, false, now() - interval '1 day', now());

            INSERT INTO "GuestCheckInSessions" ("Id", "BookingId", "OrgId", "TokenHash", "ExpiresAt", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({s.SessionB1}, {s.BookingB1}, {s.OrgB}, {s.SessionB1.ToString("N")}, now() + interval '7 days', 2, now(), now());
            """);
    }

    private static (string Previous, string AddNullable, string Backfill, string MakeRequired) GuestMigrationIds(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var addNullable = all.Single(m => m.EndsWith("_AddGuestOrgIdNullable", StringComparison.Ordinal));
        var index = all.IndexOf(addNullable);
        return (
            all[index - 1],
            addNullable,
            all.Single(m => m.EndsWith("_BackfillGuestOrgIds", StringComparison.Ordinal)),
            all.Single(m => m.EndsWith("_MakeGuestOrgIdRequired", StringComparison.Ordinal)));
    }

    private static Task<Guid> GuestOrgAsync(AppDbContext db, Guid guestId) =>
        ScalarAsync<Guid>(db, $"""SELECT "OrgId" FROM "Guests" WHERE "Id" = '{guestId}'""");

    private static Task<Guid> BookingGuestAsync(AppDbContext db, Guid bookingId) =>
        ScalarAsync<Guid>(db, $"""SELECT "GuestId" FROM "Bookings" WHERE "Id" = '{bookingId}'""");

    private static async Task<GuestRow> GuestSnapshotAsync(AppDbContext db, Guid guestId)
    {
        await using var command = await CommandAsync(db, $"""
            SELECT "Id", "FirstName", "LastName", "Email", "DateOfBirth", "DocumentNumber", "DocumentScanUrl", "Notes", "CreatedAt"
            FROM "Guests" WHERE "Id" = '{guestId}'
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new GuestRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetDateTime(8));
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
