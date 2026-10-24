using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-12 on real PostgreSQL: the <c>AddGuidedLeaseDraft</c> migration adds the columns of the guided draft to <c>LeaseContracts</c>
/// (<c>DraftData</c>, <c>DraftVersion</c>, <c>WizardMaxStep</c>, <c>ReviewConfirmedAt</c>, <c>ReviewFingerprint</c>,
/// <c>SentForSignatureAt</c>, <c>Origin</c>) and <c>Parties</c> (<c>Residence</c>, <c>DocumentsVerifiedOn</c>). The leases of the
/// database are written by SQL at the schema right before it, as the code of the time wrote them: every draft that exists was created
/// complete in one call, so each comes out of the migration at step 5 and nothing else changes — no other status, no
/// <c>UpdatedAt</c>, no value of a party; the new columns start empty or at their defaults; the backfill is idempotent and never
/// touches a draft the new code advanced; the migration reverts and reapplies on live data; a historical draft opens in the guided
/// draft with the steps whose data are complete done.
/// </summary>
public class AddGuidedLeaseDraftMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_EveryDraftThatExists_ComesOutAtStep5_AndNoOtherLeaseIsTouched()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        Assert.Equal(5, await WizardMaxStepAsync(s.Draft));
        Assert.Equal(5, await WizardMaxStepAsync(s.DraftWithoutParties));
        foreach (var other in new[] { s.AwaitingSignature, s.Signed, s.Registered, s.Rejected })
            Assert.Equal(0, await WizardMaxStepAsync(other));
    }

    [PostgresFact]
    public async Task Migration_TheNewColumns_StartEmpty_AndTheRowsKeepWhatTheyHad()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var leases = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking().Include(l => l.Parties).ToListAsync();
        Assert.Equal(6, leases.Count);
        Assert.All(leases, lease =>
        {
            Assert.Null(lease.DraftData);
            Assert.Equal(0, lease.DraftVersion);
            Assert.Equal(LeaseOrigin.Created, lease.Origin);
            Assert.Null(lease.ReviewConfirmedAt);
            Assert.Null(lease.ReviewFingerprint);
            Assert.Null(lease.SentForSignatureAt);
            // The backfill touches one column and not the clock of the row.
            Assert.Equal(Seed.UpdatedAt, lease.UpdatedAt);
            Assert.Equal(800m, lease.MonthlyRent);
        });
        Assert.Equal(LeaseStatus.Draft, leases.Single(l => l.Id == s.Draft).Status);
        Assert.Equal(LeaseStatus.Rejected, leases.Single(l => l.Id == s.Rejected).Status);

        var parties = leases.Single(l => l.Id == s.Draft).Parties.OrderBy(p => p.Role).ToList();
        Assert.Equal(["Mario", "Giulia"], parties.Select(p => p.FirstName));
        Assert.All(parties, party =>
        {
            Assert.Null(party.Residence);
            Assert.Null(party.DocumentsVerifiedOn);
        });
        Assert.Equal("VRDGLI85B02F205A", parties[1].FiscalCode);
    }

    [PostgresFact]
    public async Task Migration_AHistoricalDraft_OpensInTheGuidedDraftWithTheStepsWhoseDataAreCompleteDone()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var lease = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking().Include(l => l.Parties).SingleAsync(l => l.Id == s.Draft);
        var values = LeaseDraftProjection.ReadValues(lease);
        var flow = LeaseDraftProgress.Compute(new LeaseDraftProgressInput
        {
            Origin = lease.Origin,
            Status = lease.Status,
            WizardMaxStep = lease.WizardMaxStep,
            Values = values,
            Facts = new LeaseDraftFacts { HasApe = true, HasCadastralData = true },
            Today = new DateOnly(2026, 10, 9),
        });

        // The property, the dates and the model need nothing and the landlord has been through them: done. The parties lack the
        // residence the guided draft asks for, and the tax regime was never asked by the form of the time, nor was the deposit
        // required (LR-DEP: a contract made in CasaZen prints it): those two are to do.
        Assert.Equal(
            ["Done", "Todo", "Todo", "Done", "Done"],
            Enumerable.Range(1, 5).Select(step => flow[step].State.ToString()));
        Assert.Equal(["landlord_incomplete", "tenant_incomplete"], flow[2].Needs.Select(n => n.Code));
        Assert.Equal(["tax_regime_missing", "deposit_missing"], flow[3].Needs.Select(n => n.Code));
        Assert.Equal("2026-11-01", values.StartDate);
        Assert.Equal(800m, values.MonthlyRent);
    }

    [PostgresFact]
    public async Task Migration_AnInsertByAWriterThatDoesNotKnowTheColumns_GetsTheStartingValues()
    {
        var s = new Seed();
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await _database!.MigrateEmptyToAsync(PreviousMigration(db));
        await SeedOrgAndPropertyAsync(db, s);
        migrator.Migrate(ThisMigration(db));

        // The schema of this migration and no further: the code that ran before it writes a lease without naming the new columns.
        var late = Guid.NewGuid();
        await InsertLeaseAsync(db, s, late, LeaseStatus.Draft);

        var wizardMaxStep = await db.Database
            .SqlQuery<int>($"""SELECT "WizardMaxStep" AS "Value" FROM "LeaseContracts" WHERE "Id" = {late}""")
            .SingleAsync();
        var version = await db.Database
            .SqlQuery<int>($"""SELECT "DraftVersion" AS "Value" FROM "LeaseContracts" WHERE "Id" = {late}""")
            .SingleAsync();
        var origin = await db.Database
            .SqlQuery<int>($"""SELECT "Origin" AS "Value" FROM "LeaseContracts" WHERE "Id" = {late}""")
            .SingleAsync();
        Assert.Equal((0, 0, (int)LeaseOrigin.Created), (wizardMaxStep, version, origin));
    }

    [PostgresFact]
    public async Task BackfillSql_RunAgain_ChangesNothing()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);
        var before = await SnapshotAsync();

        await using (var db = _database!.CreateContext())
            await db.Database.ExecuteSqlRawAsync(AddGuidedLeaseDraft.BackfillSql);

        Assert.Equal(before, await SnapshotAsync());
    }

    [PostgresFact]
    public async Task BackfillSql_NeverTouchesADraftTheNewCodeAdvanced()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);
        await using (var db = _database!.CreateContext())
        {
            // A draft the guided wizard created after the migration and left at step 2; and one it advanced to the review.
            await db.Database.ExecuteSqlAsync($"""UPDATE "LeaseContracts" SET "WizardMaxStep" = 2 WHERE "Id" = {s.Draft}""");
            await db.Database.ExecuteSqlAsync($"""UPDATE "LeaseContracts" SET "WizardMaxStep" = 6 WHERE "Id" = {s.DraftWithoutParties}""");
            await db.Database.ExecuteSqlRawAsync(AddGuidedLeaseDraft.BackfillSql);
        }

        Assert.Equal(2, await WizardMaxStepAsync(s.Draft));
        Assert.Equal(6, await WizardMaxStepAsync(s.DraftWithoutParties));
    }

    [PostgresFact]
    public async Task Migration_DownThenUp_DropsAndRestoresTheColumns_AndTheDraftsComeBackAtStep5()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(PreviousMigration(db));
        var leaseColumns = await ColumnsAsync(db, "LeaseContracts");
        var partyColumns = await ColumnsAsync(db, "Parties");
        foreach (var column in new[] { "DraftData", "DraftVersion", "WizardMaxStep", "ReviewConfirmedAt", "ReviewFingerprint", "SentForSignatureAt", "Origin" })
            Assert.DoesNotContain(column, leaseColumns);
        Assert.DoesNotContain("Residence", partyColumns);
        Assert.DoesNotContain("DocumentsVerifiedOn", partyColumns);

        await db.Database.MigrateAsync();

        Assert.Equal(5, await WizardMaxStepAsync(s.Draft));
        Assert.Equal(0, await WizardMaxStepAsync(s.Signed));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migration_NewColumnsHaveTheLengthsAndTypesOfTheModel()
    {
        var s = new Seed();
        await MigrateWithSeedAsync(s);

        await using var db = _database!.CreateContext();
        var fingerprint = await db.Database
            .SqlQuery<int>($"""SELECT character_maximum_length AS "Value" FROM information_schema.columns WHERE table_name = 'LeaseContracts' AND column_name = 'ReviewFingerprint'""")
            .SingleAsync();
        var residence = await db.Database
            .SqlQuery<string>($"""SELECT data_type::text AS "Value" FROM information_schema.columns WHERE table_name = 'Parties' AND column_name = 'Residence'""")
            .SingleAsync();
        var draft = await db.Database
            .SqlQuery<string>($"""SELECT data_type::text AS "Value" FROM information_schema.columns WHERE table_name = 'LeaseContracts' AND column_name = 'DraftData'""")
            .SingleAsync();
        Assert.Equal(64, fingerprint);
        // An encrypted payload is longer than the text it protects: no length on these two.
        Assert.Equal("text", residence);
        Assert.Equal("text", draft);
    }

    // ─── Seed ───────────────────────────────────────────────────────────────────────────────────────

    private sealed class Seed
    {
        public static readonly DateTime UpdatedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        public Guid Org { get; } = Guid.NewGuid();
        public Guid Property { get; } = Guid.NewGuid();

        /// <summary>A draft with its landlord and tenant, as the complete creation of the time wrote it.</summary>
        public Guid Draft { get; } = Guid.NewGuid();
        public Guid DraftWithoutParties { get; } = Guid.NewGuid();
        public Guid AwaitingSignature { get; } = Guid.NewGuid();
        public Guid Signed { get; } = Guid.NewGuid();
        public Guid Registered { get; } = Guid.NewGuid();
        public Guid Rejected { get; } = Guid.NewGuid();
    }

    private async Task MigrateWithSeedAsync(Seed s)
    {
        await using var db = _database!.CreateContext();
        await _database!.MigrateEmptyToAsync(PreviousMigration(db));
        await SeedOrgAndPropertyAsync(db, s);

        await InsertLeaseAsync(db, s, s.Draft, LeaseStatus.Draft);
        await InsertLeaseAsync(db, s, s.DraftWithoutParties, LeaseStatus.Draft);
        await InsertLeaseAsync(db, s, s.AwaitingSignature, LeaseStatus.AwaitingSignature);
        await InsertLeaseAsync(db, s, s.Signed, LeaseStatus.Signed);
        await InsertLeaseAsync(db, s, s.Registered, LeaseStatus.Registered);
        await InsertLeaseAsync(db, s, s.Rejected, LeaseStatus.Rejected);

        await LegacyPartyRows.InsertAsync(db, new Party
        {
            LeaseContractId = s.Draft,
            Role = PartyRole.Landlord,
            FirstName = "Mario",
            LastName = "Rossi",
            FiscalCode = "RSSMRA80A01H501U",
            Citizenship = "IT",
            ContactEmail = "mario@example.com",
        });
        await LegacyPartyRows.InsertAsync(db, new Party
        {
            LeaseContractId = s.Draft,
            Role = PartyRole.Tenant,
            FirstName = "Giulia",
            LastName = "Verdi",
            FiscalCode = "VRDGLI85B02F205A",
            Citizenship = "IT",
            ContactEmail = "giulia@example.com",
        });

        await db.Database.MigrateAsync();
    }

    /// <summary>The org and the property the leases are on, written by SQL naming only the columns that exist at the schema before the migration.</summary>
    private static async Task SeedOrgAndPropertyAsync(AppDbContext db, Seed s)
    {
        await LegacyOrgRows.InsertAsync(db, new OrgEntity
        {
            Id = s.Org,
            Name = "Org LR-12",
            Slug = $"lr12-{s.Org:N}",
            DisplayName = "Org LR-12",
            ContactEmail = "lr12@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await LegacyPropertyRows.InsertAsync(db, new Property
        {
            Id = s.Property,
            OrgId = s.Org,
            OwnerId = "auth0|lr12",
            Name = "Casa LR-12",
            Description = "LR-12",
            Address = "Via LR-12 " + s.Property.ToString("N"),
            City = "Bari",
            PostalCode = "70121",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 0,
            NightlyRate = 0,
            UpdatedAt = Seed.UpdatedAt,
        });
    }

    private static Task InsertLeaseAsync(AppDbContext db, Seed s, Guid id, LeaseStatus status) =>
        LegacyLeaseRows.InsertAsync(db, new LeaseContract
        {
            Id = id,
            PropertyId = s.Property,
            OrgId = s.Org,
            Status = status,
            StartDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
            CreatedAt = Seed.UpdatedAt,
            UpdatedAt = Seed.UpdatedAt,
        });

    // ─── Reads ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> WizardMaxStepAsync(Guid leaseId)
    {
        await using var db = _database!.CreateContext();
        return await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == leaseId)
            .Select(l => l.WizardMaxStep)
            .SingleAsync();
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = _database!.CreateContext();
        var rows = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(l => l.Id)
            .Select(l => l.Id + ":" + l.WizardMaxStep + ":" + l.DraftVersion + ":" + l.UpdatedAt)
            .ToListAsync();
        return string.Join("|", rows);
    }

    private static async Task<List<string>> ColumnsAsync(AppDbContext db, string table) =>
        await db.Database
            .SqlQuery<string>($"""SELECT column_name::text AS "Value" FROM information_schema.columns WHERE table_name = {table}""")
            .ToListAsync();

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddGuidedLeaseDraft", StringComparison.Ordinal));
        Assert.True(index > 0, "AddGuidedLeaseDraft migration not found.");
        return all[index - 1];
    }

    private static string ThisMigration(AppDbContext db) =>
        db.Database.GetMigrations().Single(m => m.EndsWith("_AddGuidedLeaseDraft", StringComparison.Ordinal));
}
