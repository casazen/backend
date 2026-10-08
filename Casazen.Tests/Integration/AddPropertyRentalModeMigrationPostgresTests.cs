using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PM-01 (decision D19) on real PostgreSQL: the <c>AddPropertyRentalMode</c> migration adds <c>Properties.RentalMode</c> at
/// <c>Short</c> for every row and sets <c>Long</c> only on the live properties that have lease contracts (not draft nor
/// rejected), no booking at all and the A7-06 marker (no guests, no nightly rate). The ambiguous cases (contracts and
/// bookings) and every other combination stay <c>Short</c>; the dry run lists exactly the rows the migration changes; the
/// statement is idempotent and does not touch <c>UpdatedAt</c>. The three cases of the task: only contracts, only bookings,
/// both.
/// </summary>
public class AddPropertyRentalModeMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_OnlyContracts_BecomesLong()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        Assert.Equal(RentalMode.Long, await ModeAsync(s.OnlyContracts));
        // A lease that has ended is still a lease: the property was let long-term.
        Assert.Equal(RentalMode.Long, await ModeAsync(s.OnlyEndedContract));
    }

    [PostgresFact]
    public async Task Migration_OnlyBookings_StaysShort()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        Assert.Equal(RentalMode.Short, await ModeAsync(s.OnlyBookings));
    }

    [PostgresFact]
    public async Task Migration_ContractsAndBookings_AreAmbiguousAndStayShort()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        // With the marker or without it, with the booking cancelled or alive: the host did both, nothing is guessed.
        Assert.Equal(RentalMode.Short, await ModeAsync(s.ContractsAndBookingsWithMarker));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.ContractsAndBookingsWithShortStayData));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.ContractsAndOnlyACancelledBooking));
    }

    [PostgresFact]
    public async Task Migration_EveryOtherCombination_StaysShort()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        Assert.Equal(RentalMode.Short, await ModeAsync(s.ContractsWithShortStayData));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.ContractsWithGuestsOnly));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.ContractsWithRateOnly));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.OnlyDraftContract));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.OnlyRejectedContract));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.MarkerOnly));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.ShortStayProperty));
        // The contracts of a sibling property do not count for this one.
        Assert.Equal(RentalMode.Short, await ModeAsync(s.MarkerNextToALeasedProperty));
    }

    [PostgresFact]
    public async Task Migration_SoftDeletedProperty_IsLeftAsItWas()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        Assert.Equal(RentalMode.Short, await ModeAsync(s.DeletedWithContracts));
    }

    [PostgresFact]
    public async Task Migration_Backfill_DoesNotTouchUpdatedAtNorTheOtherColumns()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == s.OnlyContracts);
        Assert.Equal(RentalMode.Long, property.RentalMode);
        Assert.Equal(Seed.UpdatedAt, property.UpdatedAt);
        Assert.Equal(0, property.MaxGuests);
        Assert.Equal(0m, property.NightlyRate);
        Assert.True(property.IsActive);
    }

    [PostgresFact]
    public async Task Migration_NewColumnAndIndex_AreThere_AndAnInsertWithoutTheModeIsShort()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var indexes = await db.Database
            .SqlQuery<string>($"""SELECT indexname::text AS "Value" FROM pg_indexes WHERE tablename = 'Properties'""")
            .ToListAsync();
        Assert.Contains("IX_Properties_OrgId_RentalMode", indexes);

        // The column default is 0 = Short: an old writer that does not know the column still creates short-stay properties.
        var late = Guid.NewGuid();
        await InsertPropertyAsync(db, late, s.Org, "Scritta dal vecchio codice", maxGuests: 0, nightlyRate: 0, isDeleted: false);
        Assert.Equal(RentalMode.Short, await ModeAsync(late));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task BackfillSql_RunAgain_ChangesNothing()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);
        var before = await SnapshotAsync();

        await using (var db = _database!.CreateContext())
            await db.Database.ExecuteSqlRawAsync(AddPropertyRentalMode.BackfillSql);

        Assert.Equal(before, await SnapshotAsync());
    }

    [PostgresFact]
    public async Task BackfillSql_ARowSetToShortByHandAfterTheMigration_IsPutBackToLongByARerun()
    {
        // The statement is the rule, not a one-off: it updates what is still at Short and fits the three signals. (This
        // is why a wrongly classified row is fixed by hand only after the last run of the migration, runbook section 4.)
        var s = new Seed();
        await MigrateWithSeedAsync(s);
        await using (var db = _database!.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "RentalMode" = 0 WHERE "Id" = {s.OnlyContracts}""");
            await db.Database.ExecuteSqlRawAsync(AddPropertyRentalMode.BackfillSql);
        }

        Assert.Equal(RentalMode.Long, await ModeAsync(s.OnlyContracts));
    }

    [PostgresFact]
    public async Task DryRun_ListsExactlyTheRowsTheMigrationChanges()
    {
        var s = new Seed();
        List<DryRunRow> detail;
        List<DryRunSummary> summary;
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await SeedAsync(db, s);

            // The dry run runs BEFORE the migration, on the database as it is.
            detail = await ReadDetailAsync(db);
            summary = await ReadSummaryAsync(db);

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var changed = await after.Properties.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.RentalMode == RentalMode.Long)
            .Select(p => p.Id)
            .ToListAsync();

        Assert.Equal(
            changed.Order(),
            detail.Where(r => r.Case == "to_long").Select(r => r.PropertyId).Order());
        Assert.Equal(
            new[] { s.ContractsAndBookingsWithMarker, s.ContractsAndBookingsWithShortStayData, s.ContractsAndOnlyACancelledBooking }
                .Order(),
            detail.Where(r => r.Case == "ambiguous").Select(r => r.PropertyId).Order());
        Assert.Equal(
            new[] { s.ContractsWithShortStayData, s.ContractsWithGuestsOnly, s.ContractsWithRateOnly, s.LeasedNeighbour }.Order(),
            detail.Where(r => r.Case == "contracts_with_short_stay_data").Select(r => r.PropertyId).Order());
        // Soft-deleted properties, drafts and rejected leases are not in the list at all.
        Assert.DoesNotContain(detail, r => r.PropertyId == s.DeletedWithContracts);
        Assert.DoesNotContain(detail, r => r.PropertyId == s.OnlyDraftContract || r.PropertyId == s.OnlyRejectedContract);

        // The summary: one row per org with contracts, then the total (no org).
        var total = Assert.Single(summary, row => row.OrgId is null);
        Assert.Equal(changed.Count, total.ToLong);
        Assert.Equal(3, total.Ambiguous);
        Assert.Equal(4, total.ContractsWithShortStayData);
        Assert.Equal(summary.Where(row => row.OrgId is not null).Sum(row => row.ToLong), total.ToLong);
        var ownOrg = Assert.Single(summary, row => row.OrgId == s.Org);
        Assert.Equal(changed.Count, ownOrg.ToLong);
        Assert.Equal(summary.Last(), total);
    }

    [PostgresFact]
    public async Task Migration_DownThenUp_DropsAndRestoresTheColumn()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(PreviousMigration(db));
        var columns = await db.Database
            .SqlQuery<string>($"""SELECT column_name::text AS "Value" FROM information_schema.columns WHERE table_name = 'Properties'""")
            .ToListAsync();
        Assert.DoesNotContain("RentalMode", columns);

        await db.Database.MigrateAsync();
        // Up again on the data that is there: the same rows are backfilled.
        Assert.Equal(RentalMode.Long, await ModeAsync(s.OnlyContracts));
        Assert.Equal(RentalMode.Short, await ModeAsync(s.OnlyBookings));
    }

    // ─── Seed ───────────────────────────────────────────────────────────────────────────────────────

    private sealed class Seed
    {
        public static readonly DateTime UpdatedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        public Guid Org { get; } = Guid.NewGuid();
        public Guid Guest { get; } = Guid.NewGuid();

        // The three cases of the task.
        public Guid OnlyContracts { get; } = Guid.NewGuid();
        public Guid OnlyBookings { get; } = Guid.NewGuid();
        public Guid ContractsAndBookingsWithMarker { get; } = Guid.NewGuid();

        // Variants.
        public Guid OnlyEndedContract { get; } = Guid.NewGuid();
        public Guid ContractsAndBookingsWithShortStayData { get; } = Guid.NewGuid();
        public Guid ContractsAndOnlyACancelledBooking { get; } = Guid.NewGuid();
        public Guid ContractsWithShortStayData { get; } = Guid.NewGuid();
        public Guid ContractsWithGuestsOnly { get; } = Guid.NewGuid();
        public Guid ContractsWithRateOnly { get; } = Guid.NewGuid();
        public Guid OnlyDraftContract { get; } = Guid.NewGuid();
        public Guid OnlyRejectedContract { get; } = Guid.NewGuid();
        public Guid MarkerOnly { get; } = Guid.NewGuid();
        public Guid ShortStayProperty { get; } = Guid.NewGuid();
        public Guid MarkerNextToALeasedProperty { get; } = Guid.NewGuid();
        public Guid LeasedNeighbour { get; } = Guid.NewGuid();
        public Guid DeletedWithContracts { get; } = Guid.NewGuid();
    }

    private async Task MigrateWithSeedAsync(Seed s)
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        await SeedAsync(db, s);
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// The org, its guest and the properties of every combination of the signals, at the schema right before the migration.
    /// The properties are raw rows (the entity writes the column that the migration adds); the leases and the bookings are
    /// entities, since the migration does not touch their tables.
    /// </summary>
    private static async Task SeedAsync(AppDbContext db, Seed s)
    {
        db.Orgs.Add(new OrgEntity
        {
            Id = s.Org,
            Name = "Org PM-01",
            Slug = $"pm01-{s.Org:N}",
            DisplayName = "Org PM-01",
            ContactEmail = "pm01@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        db.Guests.Add(new Guest
        {
            Id = s.Guest,
            OrgId = s.Org,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario.{Guid.NewGuid():N}@example.com",
        });
        await db.SaveChangesAsync();

        // maxGuests 0 and nightlyRate 0 = the A7-06 marker.
        await InsertPropertyAsync(db, s.OnlyContracts, s.Org, "Solo contratti", 0, 0);
        await InsertPropertyAsync(db, s.OnlyEndedContract, s.Org, "Solo contratto finito", 0, 0);
        await InsertPropertyAsync(db, s.OnlyBookings, s.Org, "Solo prenotazioni", 4, 100);
        await InsertPropertyAsync(db, s.ContractsAndBookingsWithMarker, s.Org, "Contratti e prenotazioni, marcatore", 0, 0);
        await InsertPropertyAsync(db, s.ContractsAndBookingsWithShortStayData, s.Org, "Contratti e prenotazioni, tariffa", 4, 100);
        await InsertPropertyAsync(db, s.ContractsAndOnlyACancelledBooking, s.Org, "Contratti e prenotazione annullata", 0, 0);
        await InsertPropertyAsync(db, s.ContractsWithShortStayData, s.Org, "Contratti con tariffa", 4, 100);
        await InsertPropertyAsync(db, s.ContractsWithGuestsOnly, s.Org, "Contratti con ospiti", 2, 0);
        await InsertPropertyAsync(db, s.ContractsWithRateOnly, s.Org, "Contratti con tariffa senza ospiti", 0, 80);
        await InsertPropertyAsync(db, s.OnlyDraftContract, s.Org, "Solo bozza", 0, 0);
        await InsertPropertyAsync(db, s.OnlyRejectedContract, s.Org, "Solo contratto rifiutato", 0, 0);
        await InsertPropertyAsync(db, s.MarkerOnly, s.Org, "Solo marcatore", 0, 0);
        await InsertPropertyAsync(db, s.ShortStayProperty, s.Org, "Affitto breve", 6, 150);
        await InsertPropertyAsync(db, s.MarkerNextToALeasedProperty, s.Org, "Marcatore accanto a un locato", 0, 0);
        await InsertPropertyAsync(db, s.LeasedNeighbour, s.Org, "Locato accanto", 3, 90);
        await InsertPropertyAsync(db, s.DeletedWithContracts, s.Org, "Eliminato con contratti", 0, 0, isDeleted: true);

        Lease(db, s, s.OnlyContracts, LeaseStatus.Signed, 2026, 2030);
        Lease(db, s, s.OnlyEndedContract, LeaseStatus.Registered, 2020, 2024);
        Lease(db, s, s.ContractsAndBookingsWithMarker, LeaseStatus.Registered, 2025, 2029);
        Lease(db, s, s.ContractsAndBookingsWithShortStayData, LeaseStatus.Signed, 2025, 2029);
        Lease(db, s, s.ContractsAndOnlyACancelledBooking, LeaseStatus.Signed, 2025, 2029);
        Lease(db, s, s.ContractsWithShortStayData, LeaseStatus.AwaitingSignature, 2026, 2030);
        Lease(db, s, s.ContractsWithGuestsOnly, LeaseStatus.PartiallySigned, 2026, 2030);
        Lease(db, s, s.ContractsWithRateOnly, LeaseStatus.RegistrationPending, 2026, 2030);
        Lease(db, s, s.OnlyDraftContract, LeaseStatus.Draft, 2026, 2030);
        Lease(db, s, s.OnlyRejectedContract, LeaseStatus.Rejected, 2026, 2030);
        Lease(db, s, s.LeasedNeighbour, LeaseStatus.SentToProvider, 2026, 2030);
        Lease(db, s, s.DeletedWithContracts, LeaseStatus.Signed, 2026, 2030);

        Booking(db, s, s.OnlyBookings, BookingStatus.Confirmed);
        Booking(db, s, s.ContractsAndBookingsWithMarker, BookingStatus.CheckedOut);
        Booking(db, s, s.ContractsAndBookingsWithShortStayData, BookingStatus.Confirmed);
        Booking(db, s, s.ContractsAndOnlyACancelledBooking, BookingStatus.Cancelled);
        Booking(db, s, s.ShortStayProperty, BookingStatus.Pending);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>A live or deleted property row with the columns of the schema before the migration.</summary>
    private static Task InsertPropertyAsync(
        AppDbContext db, Guid id, Guid orgId, string name, int maxGuests, decimal nightlyRate, bool isDeleted = false) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "IsDeleted", "CreatedAt", "UpdatedAt")
            VALUES ({id}, 'auth0|pm01', {orgId}, {name}, 'PM-01', {"Via PM-01 " + id.ToString("N")}, 'Monza', '20900',
                0, 0, 1, 1, {maxGuests}, {nightlyRate}, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, {isDeleted}, now(), {Seed.UpdatedAt});
            """);

    private static void Lease(AppDbContext db, Seed s, Guid propertyId, LeaseStatus status, int fromYear, int toYear) =>
        db.LeaseContracts.Add(new LeaseContract
        {
            PropertyId = propertyId,
            OrgId = s.Org,
            Status = status,
            StartDate = new DateTime(fromYear, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(toYear, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
        });

    private static void Booking(AppDbContext db, Seed s, Guid propertyId, BookingStatus status) =>
        db.Bookings.Add(new Booking
        {
            PropertyId = propertyId,
            OrgId = s.Org,
            GuestId = s.Guest,
            CheckInDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
        });

    // ─── Reads ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<RentalMode> ModeAsync(Guid propertyId)
    {
        await using var db = _database!.CreateContext();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => p.RentalMode)
            .SingleAsync();
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = _database!.CreateContext();
        var rows = await db.Properties.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => p.Id + ":" + (int)p.RentalMode + ":" + p.UpdatedAt)
            .ToListAsync();
        return string.Join("|", rows);
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddPropertyRentalMode", StringComparison.Ordinal));
        Assert.True(index > 0, "AddPropertyRentalMode migration not found.");
        return all[index - 1];
    }

    private sealed record DryRunRow(Guid OrgId, Guid PropertyId, string Case, long Contracts, long Bookings);

    private sealed record DryRunSummary(Guid? OrgId, long ToLong, long Ambiguous, long ContractsWithShortStayData);

    private static async Task<List<DryRunRow>> ReadDetailAsync(AppDbContext db)
    {
        var rows = new List<DryRunRow>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = AddPropertyRentalMode.DryRunDetailSql;
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DryRunRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        }

        return rows;
    }

    private static async Task<List<DryRunSummary>> ReadSummaryAsync(AppDbContext db)
    {
        var rows = new List<DryRunSummary>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = AddPropertyRentalMode.DryRunSummarySql;
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DryRunSummary(
                reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return rows;
    }
}
