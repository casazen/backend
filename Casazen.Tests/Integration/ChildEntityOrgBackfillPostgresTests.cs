using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-2: applies every migration up to the one before <c>AddChildEntityOrgIdNullable</c> on a real
/// PostgreSQL database, inserts child rows the way the pre-TN-2 code wrote them (no OrgId; a check-in
/// session with an org different from its booking's), then applies the three TN-2 migrations and checks
/// that every child row carries its parent's org, with OrgId NOT NULL and a Restrict FK to Orgs.
/// </summary>
public class ChildEntityOrgBackfillPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task ChildEntityOrgMigrations_RowsOfTwoOrgs_CopyTheParentOrgAndBecomeRequired()
    {
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var (previous, _, backfill, _) = MigrationIds(db);
        migrator.Migrate(previous);

        var s = new Seed();
        await InsertPreTn2StateAsync(db, s);

        migrator.Migrate(backfill);
        await db.Database.MigrateAsync();

        Assert.Equal(s.OrgA, await OrgOfAsync(db, "PropertyDocuments", s.DocumentA));
        Assert.Equal(s.OrgB, await OrgOfAsync(db, "PropertyDocuments", s.DocumentB));
        Assert.Equal(s.OrgA, await OrgOfAsync(db, "OtaIntegrations", s.IntegrationA));
        Assert.Equal(s.OrgB, await OrgOfAsync(db, "PricingAdapterConfigs", s.PricingConfigB));
        Assert.Equal(s.OrgB, await OrgOfAsync(db, "PricingHistories", s.PricingHistoryB));
        Assert.Equal(s.OrgA, await OrgOfAsync(db, "AlloggiatiWebReports", s.ReportA));
        // Realigned to its booking: the pre-TN-2 row pointed at the wrong org.
        Assert.Equal(s.OrgB, await OrgOfAsync(db, "GuestCheckInSessions", s.SessionB));

        foreach (var table in new[] { "PropertyDocuments", "OtaIntegrations", "PricingAdapterConfigs", "PricingHistories", "AlloggiatiWebReports", "GuestCheckInSessions" })
        {
            Assert.Equal("NO", await ScalarAsync<string>(db, $"""
                SELECT is_nullable FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = '{table}' AND column_name = 'OrgId'
                """));
            Assert.Equal("r", await ScalarAsync<string>(db, $"""
                SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_{table}_Orgs_OrgId'
                """));
        }

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task MakeChildEntityOrgIdsRequired_ChildRowStillWithoutOrg_FailsPreFlightBeforeNotNull()
    {
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var (previous, _, backfill, makeRequired) = MigrationIds(db);
        migrator.Migrate(previous);
        var s = new Seed();
        await InsertPreTn2StateAsync(db, s);
        migrator.Migrate(backfill);

        // A document written after the backfill by code that does not set OrgId yet.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PropertyDocuments" ("Id", "PropertyId", "FileName", "StorageUrl", "DocumentType", "UploadedBy", "UploadedAt")
            VALUES ({Guid.NewGuid()}, {s.PropertyA}, 'late.pdf', 'documents/late.pdf', 'Other', 'auth0|late', now());
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Task.Run(() => migrator.Migrate(makeRequired)));

        Assert.Contains("Pre-flight failed", ex.MessageText);
        Assert.DoesNotContain(makeRequired, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal("YES", await ScalarAsync<string>(db, """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'PropertyDocuments' AND column_name = 'OrgId'
            """));
    }

    private sealed class Seed
    {
        public Guid OrgA { get; } = Guid.NewGuid();
        public Guid OrgB { get; } = Guid.NewGuid();
        public Guid PropertyA { get; } = Guid.NewGuid();
        public Guid PropertyB { get; } = Guid.NewGuid();
        public Guid GuestA { get; } = Guid.NewGuid();
        public Guid GuestB { get; } = Guid.NewGuid();
        public Guid BookingA { get; } = Guid.NewGuid();
        public Guid BookingB { get; } = Guid.NewGuid();
        public Guid DocumentA { get; } = Guid.NewGuid();
        public Guid DocumentB { get; } = Guid.NewGuid();
        public Guid IntegrationA { get; } = Guid.NewGuid();
        public Guid PricingConfigB { get; } = Guid.NewGuid();
        public Guid PricingHistoryB { get; } = Guid.NewGuid();
        public Guid ReportA { get; } = Guid.NewGuid();
        public Guid SessionB { get; } = Guid.NewGuid();
    }

    /// <summary>The schema right before AddChildEntityOrgIdNullable: the child tables have no OrgId yet.</summary>
    private static async Task InsertPreTn2StateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.OrgA}, 'Org A', {"tn2-a-" + s.OrgA.ToString("N")}, 0, 'Org A', '', true, now(), now()),
              ({s.OrgB}, 'Org B', {"tn2-b-" + s.OrgB.ToString("N")}, 0, 'Org B', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.PropertyA}, 'auth0|tn2-a', {s.OrgA}, 'A1', 'Property A', 'Via A', 'Roma', '00100', 0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now()),
              ({s.PropertyB}, 'auth0|tn2-b', {s.OrgB}, 'B1', 'Property B', 'Via B', 'Milano', '20100', 0, 0, 1, 1, 2, 120, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "Guests" (
                "Id", "OrgId", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress", "Notes",
                "ConsentVersion", "MarketingConsent", "ErasureRequested", "DataRetentionUntil", "DataProcessingPurpose",
                "IsDeleted", "DeletionReason", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.GuestA}, {s.OrgA}, 'Mario', 'Rossi', 'mario@example.com', '', '', '', '', '', '', '', '', '', '', '',
               '', false, false, now() + interval '7 years', 'booking', false, '', now(), now()),
              ({s.GuestB}, {s.OrgB}, 'Anna', 'Verdi', 'anna@example.com', '', '', '', '', '', '', '', '', '', '', '',
               '', false, false, now() + interval '7 years', 'booking', false, '', now(), now());

            INSERT INTO "Bookings" (
                "Id", "PropertyId", "GuestId", "OrgId", "CheckInDate", "CheckOutDate",
                "NumberOfGuests", "Status", "Source", "ExternalId", "BasePrice", "TouristTax", "TotalPrice",
                "TouristTaxAmount", "NumberOfAdults", "NumberOfChildren", "SpecialRequests", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.BookingA}, {s.PropertyA}, {s.GuestA}, {s.OrgA}, now(), now() + interval '2 days', 1, 1, 0, '', 0, 0, 200, 0, 1, 0, '', now(), now()),
              ({s.BookingB}, {s.PropertyB}, {s.GuestB}, {s.OrgB}, now(), now() + interval '2 days', 1, 1, 0, '', 0, 0, 240, 0, 1, 0, '', now(), now());

            INSERT INTO "PropertyDocuments" ("Id", "PropertyId", "FileName", "StorageUrl", "DocumentType", "UploadedBy", "UploadedAt")
            VALUES
              ({s.DocumentA}, {s.PropertyA}, 'cin.pdf', 'documents/a/cin.pdf', 'CinCertificate', 'auth0|tn2-a', now()),
              ({s.DocumentB}, {s.PropertyB}, 'plan.pdf', 'documents/b/plan.pdf', 'FloorPlan', 'auth0|tn2-b', now());

            INSERT INTO "OtaIntegrations" ("Id", "PropertyId", "Platform", "ExternalPropertyId", "ApiKey", "ApiSecret",
                "IsActive", "SyncEnabled", "LastSyncAt", "CreatedAt", "UpdatedAt")
            VALUES ({s.IntegrationA}, {s.PropertyA}, 'Airbnb', 'ext-a', '', '', true, true, now(), now(), now());

            INSERT INTO "PricingAdapterConfigs" ("Id", "PropertyId", "IsEnabled", "AdaptationFrequency",
                "IncludeSeasonality", "IncludePublicHolidays", "CreatedAt", "UpdatedAt")
            VALUES ({s.PricingConfigB}, {s.PropertyB}, true, 'daily', true, true, now(), now());

            INSERT INTO "PricingHistories" ("Id", "PropertyId", "AdaptationDate", "PreviousPrice", "NewPrice",
                "ChangeReason", "AiConfidence", "OtasSynced", "SyncStatus", "CreatedAt")
            VALUES ({s.PricingHistoryB}, {s.PropertyB}, now(), 120, 130, 'season', 0.5, '', 'Pending', now());

            INSERT INTO "AlloggiatiWebReports" ("Id", "BookingId", "GuestId", "ReportedAt", "Status", "RetryCount", "ManuallyCompleted", "CreatedAt", "UpdatedAt")
            VALUES ({s.ReportA}, {s.BookingA}, {s.GuestA}, now(), 0, 0, false, now(), now());

            INSERT INTO "GuestCheckInSessions" ("Id", "BookingId", "OrgId", "TokenHash", "ExpiresAt", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({s.SessionB}, {s.BookingB}, {s.OrgA}, {s.SessionB.ToString("N")}, now() + interval '7 days', 0, now(), now());
            """);
    }

    private static (string Previous, string AddNullable, string Backfill, string MakeRequired) MigrationIds(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var addNullable = all.Single(m => m.EndsWith("_AddChildEntityOrgIdNullable", StringComparison.Ordinal));
        var index = all.IndexOf(addNullable);
        return (
            all[index - 1],
            addNullable,
            all.Single(m => m.EndsWith("_BackfillChildEntityOrgIds", StringComparison.Ordinal)),
            all.Single(m => m.EndsWith("_MakeChildEntityOrgIdsRequired", StringComparison.Ordinal)));
    }

    // Test-only SQL built from constant table names and GUIDs generated in this class, never from input.
    private static Task<Guid> OrgOfAsync(AppDbContext db, string table, Guid id) =>
        ScalarAsync<Guid>(db, $"""SELECT "OrgId" FROM "{table}" WHERE "Id" = '{id}'""");

    private static async Task<T> ScalarAsync<T>(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)value;
    }
}
