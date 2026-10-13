using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-04 (A7-04): on a real PostgreSQL database, the <c>AddLeaseStipulaDate</c> migration takes the stipula from the
/// first AllPartiesSigned event (Europe/Rome date), recomputes the RLI deadline as min(stipula, start) + 30 days, leaves
/// stipula and deadline null when no signing was recorded, and binds the reminders of the old job to the deadline they
/// were sent for.
/// </summary>
public class AddLeaseStipulaDateMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddLeaseStipulaDate_ExistingLeases_BackfillsStipulaAndCorrectsDeadline()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var s = new Seed();
        await InsertPreviousStateAsync(db, s);

        await db.Database.MigrateAsync();

        // Signed 1/8 in Rome (22:30 UTC on 31/7), start 1/10: stipula 1/8, deadline 31/8 (was 31/10).
        Assert.Equal(("2026-08-01", "2026-08-31"), await DatesAsync(db, s.SignedBeforeStart));
        // Signed 20/6, start 1/6 (earlier): deadline from the start, 1/7.
        Assert.Equal(("2026-06-20", "2026-07-01"), await DatesAsync(db, s.RegisteredStartFirst));
        // Signed without a signing event: to be determined, never guessed.
        Assert.Equal(((string?)null, (string?)null), await DatesAsync(db, s.SignedWithoutEvent));
        // Draft: no stipula, the old StartDate + 30 removed (the API resolves it once the start date has come).
        Assert.Equal(((string?)null, (string?)null), await DatesAsync(db, s.Draft));

        // Old reminders bound to the deadline they were computed on; other payloads untouched.
        Assert.Equal(
            ["extra-eu", "t-15:2026-10-31"],
            await PayloadsAsync(db, s.SignedBeforeStart));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private sealed class Seed
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Property { get; } = Guid.NewGuid();
        public Guid SignedBeforeStart { get; } = Guid.NewGuid();
        public Guid RegisteredStartFirst { get; } = Guid.NewGuid();
        public Guid SignedWithoutEvent { get; } = Guid.NewGuid();
        public Guid Draft { get; } = Guid.NewGuid();
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddLeaseStipulaDate", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    /// <summary>
    /// Leases as the old code stored them: deadline StartDate + 30, AllPartiesSigned (3) events, reminders (12) with the
    /// old payloads.
    /// </summary>
    private static async Task InsertPreviousStateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Org}, 'Org LT-04', {"lt04-" + s.Org.ToString("N")}, 0, 'Org LT-04', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Property}, 'auth0|lt04', {s.Org}, 'Casa', 'Casa LT-04', 'Via Roma 1', 'Seveso', '20822', 0, 0, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "LeaseContracts" (
                "Id", "PropertyId", "OrgId", "Status", "FiscalRegime", "StartDate", "EndDate", "MonthlyRent",
                "RegistrationDeadline", "SignedPdfStoragePath", "ErasureRequested", "DataRetentionUntil", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.SignedBeforeStart},    {s.Property}, {s.Org}, 3, 0, '2026-10-01', '2030-09-30', 800, '2026-10-31', '/signed/a.pdf', false, '2036-10-01', now(), now()),
              ({s.RegisteredStartFirst}, {s.Property}, {s.Org}, 6, 0, '2026-06-01', '2030-05-31', 800, '2026-07-01', '/signed/b.pdf', false, '2036-06-01', now(), now()),
              ({s.SignedWithoutEvent},   {s.Property}, {s.Org}, 3, 0, '2026-09-01', '2030-08-31', 800, '2026-10-01', '/signed/c.pdf', false, '2036-09-01', now(), now()),
              ({s.Draft},                {s.Property}, {s.Org}, 0, 0, '2026-09-01', '2030-08-31', 800, '2026-10-01', NULL, false, '2036-09-01', now(), now());

            INSERT INTO "LeaseEvents" ("Id", "LeaseContractId", "EventType", "OccurredAt", "Payload")
            VALUES
              (gen_random_uuid(), {s.SignedBeforeStart},    3,  '2026-07-31T22:30:00Z', NULL),
              (gen_random_uuid(), {s.SignedBeforeStart},    12, '2026-08-02T08:00:00Z', 'extra-eu'),
              (gen_random_uuid(), {s.SignedBeforeStart},    12, '2026-10-16T08:00:00Z', 't-15'),
              (gen_random_uuid(), {s.RegisteredStartFirst}, 3,  '2026-06-20T10:00:00Z', NULL);
            """);
    }

    /// <summary>(StipulaDate, RegistrationDeadline) as UTC dates.</summary>
    private static async Task<(string? Stipula, string? Deadline)> DatesAsync(AppDbContext db, Guid leaseId)
    {
        var lease = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == leaseId);
        Assert.True(lease.StipulaDate is null || lease.StipulaDate.Value.TimeOfDay == TimeSpan.Zero);
        return (lease.StipulaDate?.ToString("yyyy-MM-dd"), lease.RegistrationDeadline?.ToString("yyyy-MM-dd"));
    }

    private static Task<List<string?>> PayloadsAsync(AppDbContext db, Guid leaseId) =>
        db.LeaseEvents.AsNoTracking()
            .Where(e => e.LeaseContractId == leaseId && e.EventType == Core.Entities.Enums.LeaseEventType.DeadlineReminderSent)
            .OrderBy(e => e.Payload)
            .Select(e => e.Payload)
            .ToListAsync();
}
