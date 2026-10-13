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
/// LR-01 (B1) on real PostgreSQL: the migration <see cref="AddRentRegisterAndReminders"/> adds the index of the rent register
/// <c>(OrgId, DueDate)</c> and the two columns of the reminders to <c>RentLedgerEntries</c>. It is additive: the installments that
/// exist before it keep every value and get "never reminded" (<c>LastReminderAt</c> null, <c>ReminderCount</c> 0); the older index is
/// kept; a writer that does not know the columns (the previous release while the migration is applied) still inserts; the migration
/// reverts and applies again. The rows before it are written by SQL with the columns of that point (<see cref="LegacyRentRows"/>),
/// never through the model, which writes the columns the migration adds.
/// </summary>
public class AddRentRegisterAndRemindersMigrationPostgresTests : IAsyncLifetime
{
    private static readonly string[] NewColumns = ["LastReminderAt", "ReminderCount"];

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_InstallmentsOfBefore_KeepTheirDataAndHaveNeverBeenReminded()
    {
        var seed = await MigrateToTheMigrationWithInstallmentsAsync();

        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        var installments = await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.DueDate).ToListAsync();
        Assert.Equal(3, installments.Count);
        Assert.All(installments, e =>
        {
            Assert.Null(e.LastReminderAt);
            Assert.Equal(0, e.ReminderCount);
            Assert.Equal(seed.Org, e.OrgId);
            Assert.Equal(seed.Lease, e.LeaseContractId);
        });
        // What the installments were before is what they are after.
        Assert.Equal([RentLedgerStatus.Paid, RentLedgerStatus.Scheduled, RentLedgerStatus.Cancelled], installments.Select(e => e.Status));
        Assert.Equal([new DateOnly(2026, 8, 5), new DateOnly(2026, 9, 5), new DateOnly(2026, 10, 5)], installments.Select(e => e.DueDate));
        Assert.Equal([900m, 900m, 900m], installments.Select(e => e.AmountDue));
        Assert.Equal(seed.Created, installments[0].CreatedAt);
    }

    [PostgresFact]
    public async Task Migration_CreatesTheIndexOfTheRegister_AndKeepsTheOlderOneOfTheOrg()
    {
        await MigrateToTheMigrationWithInstallmentsAsync();

        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        var indexes = await IndexDefinitionsAsync(db);
        var register = Assert.Single(indexes, i => i.Name == "IX_RentLedgerEntries_OrgId_DueDate");
        // The org and then the due date, in that order: the month of an org is one range of the index, already in due date order.
        Assert.Contains("(\"OrgId\", \"DueDate\")", register.Definition, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE", register.Definition, StringComparison.Ordinal);
        Assert.Contains(indexes, i => i.Name == "IX_RentLedgerEntries_OrgId");
        Assert.Contains(indexes, i => i.Name == "IX_RentLedgerEntries_LeaseContractId_PeriodStart");
    }

    [PostgresFact]
    public async Task Migration_AWriterThatDoesNotKnowTheColumns_StillInsertsAndGetsTheirDefaults()
    {
        var seed = await MigrateToTheMigrationWithInstallmentsAsync();

        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        // What the release before LR-01 writes while the migration is already applied: none of the new columns.
        await LegacyRentRows.InsertInstallmentAsync(db, Installment(seed, id, "2026-11-05", RentLedgerStatus.Scheduled));

        var stored = await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Null(stored.LastReminderAt);
        Assert.Equal(0, stored.ReminderCount);
    }

    [PostgresFact]
    public async Task Migration_RevertsAndAppliesAgain_WithoutTouchingTheInstallments()
    {
        var seed = await MigrateToTheMigrationWithInstallmentsAsync();
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        Assert.Equal(NewColumns.Length, await CountColumnsAsync(db));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        // A reminder recorded after the migration is lost with the columns when it is reverted: nothing else is.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "RentLedgerEntries" SET "LastReminderAt" = now(), "ReminderCount" = 2 WHERE "Id" = {seed.Installments[1]}""");

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));

        Assert.Equal(0, await CountColumnsAsync(db));
        Assert.DoesNotContain(await IndexDefinitionsAsync(db), i => i.Name == "IX_RentLedgerEntries_OrgId_DueDate");
        Assert.Equal(3, await db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "RentLedgerEntries" """).SingleAsync());
        Assert.Equal(
            2700m,
            await db.Database.SqlQuery<decimal>($"""SELECT sum("AmountDue") AS "Value" FROM "RentLedgerEntries" """).SingleAsync());

        await db.Database.MigrateAsync();

        Assert.Equal(NewColumns.Length, await CountColumnsAsync(db));
        var again = await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == seed.Installments[1]);
        Assert.Null(again.LastReminderAt);
        Assert.Equal(0, again.ReminderCount);
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private sealed record Seed(Guid Org, Guid Lease, Guid Schedule, IReadOnlyList<Guid> Installments, DateTime Created);

    /// <summary>
    /// Migrates to the migration right before LR-01 and writes an org, a property, a lease, its schedule and three installments
    /// (paid, scheduled, cancelled) the way that release did.
    /// </summary>
    private async Task<Seed> MigrateToTheMigrationWithInstallmentsAsync()
    {
        await using var db = _database!.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));

        var created = new DateTime(2026, 7, 1, 8, 30, 0, DateTimeKind.Utc);
        var org = new OrgEntity { Name = "Casa Rossi", Slug = $"casa-rossi-{Guid.NewGuid():N}", DisplayName = "Casa Rossi", ContactEmail = "info@casarossi.example", IsActive = true };
        await LegacyOrgRows.InsertAsync(db, org);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|lr01-migration",
            Name = "Bilocale Sparano",
            Description = "Bilocale",
            Address = "Via Sparano 1",
            City = "Bari",
            PostalCode = "70121",
            Bedrooms = 2,
            Bathrooms = 1,
            CreatedAt = created,
            UpdatedAt = created,
        };
        await LegacyPropertyRows.InsertAsync(db, property);
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Registered,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 5, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
            CreatedAt = created,
            UpdatedAt = created,
        };
        await LegacyLeaseRows.InsertAsync(db, lease);
        var schedule = new RentSchedule
        {
            OrgId = org.Id,
            LeaseContractId = lease.Id,
            Cadence = RentCadence.Monthly,
            BillingDayOfMonth = 5,
            Amount = 900m,
            NextRunDate = new DateOnly(2026, 8, 5),
            IsActive = true,
            CreatedAt = created,
            UpdatedAt = created,
        };
        await LegacyRentRows.InsertScheduleAsync(db, schedule);

        var seed = new Seed(org.Id, lease.Id, schedule.Id, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], created);
        await LegacyRentRows.InsertInstallmentAsync(db, Installment(seed, seed.Installments[0], "2026-08-05", RentLedgerStatus.Paid));
        await LegacyRentRows.InsertInstallmentAsync(db, Installment(seed, seed.Installments[1], "2026-09-05", RentLedgerStatus.Scheduled));
        await LegacyRentRows.InsertInstallmentAsync(db, Installment(seed, seed.Installments[2], "2026-10-05", RentLedgerStatus.Cancelled));
        return seed;
    }

    private static RentLedgerEntry Installment(Seed seed, Guid id, string due, RentLedgerStatus status)
    {
        var dueDate = DateOnly.Parse(due);
        return new RentLedgerEntry
        {
            Id = id,
            OrgId = seed.Org,
            LeaseContractId = seed.Lease,
            RentScheduleId = seed.Schedule,
            PeriodStart = new DateOnly(dueDate.Year, dueDate.Month, 1),
            PeriodEnd = new DateOnly(dueDate.Year, dueDate.Month, 1).AddMonths(1).AddDays(-1),
            DueDate = dueDate,
            AmountDue = 900m,
            Status = status,
            CreatedAt = seed.Created,
            UpdatedAt = seed.Created,
        };
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddRentRegisterAndReminders", StringComparison.Ordinal));
        Assert.True(index > 0, "AddRentRegisterAndReminders migration not found.");
        return all[index - 1];
    }

    private static async Task<int> CountColumnsAsync(AppDbContext db)
    {
        var names = NewColumns;
        return (int)await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM information_schema.columns WHERE table_name = 'RentLedgerEntries' AND column_name = ANY({names})")
            .SingleAsync();
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
