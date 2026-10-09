using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04 migration (<see cref="AddServiceRequestSchedule"/>) on real PostgreSQL: it applies and reverts, a row written by code that
/// does not know the new columns (the previous release while the migration is applied) still gets their defaults, the checks
/// refuse the rows the rules never write, the service of the catalog is kept as a link that clears itself when the service is
/// deleted, the statuses and the actors are stored as the explicit integers of the enums, and the index of the inbox exists.
/// </summary>
public class AddServiceRequestSchedulePostgresTests : IAsyncLifetime
{
    private static readonly string[] NewColumns =
    [
        "ScheduledStartUtc", "ScheduledEndUtc", "ServiceListingId", "ServiceNameSnapshot", "OptionsJson", "EstimatedAmountCents",
        "QuotedAmountCents", "FinalAmountCents", "PriceLinesJson", "FinalAmountNeedsConfirmation", "ResponseDueAt", "StartedAt",
        "CancelledAt", "CancelledBy", "CancellationReason", "CompletionNotes", "WorkPhotosJson", "LastRemindedAt",
        "ProposedStartUtc", "ProposedEndUtc", "ProposedAt", "ProposedByUserId", "ProposalMessage",
    ];

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_AppliesAndRevertsOnARealDatabase()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        Assert.Equal(NewColumns.Length, await CountColumnsAsync(db));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        Assert.Equal(0, await CountColumnsAsync(db));
        Assert.Equal(0, await CountObjectsAsync(db, "pg_indexes", "indexname", "IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc"));
        Assert.Equal(0, await CountObjectsAsync(db, "pg_constraint", "conname", "CK_ServiceRequests_ScheduledInterval"));

        await db.Database.MigrateAsync();
        Assert.Equal(NewColumns.Length, await CountColumnsAsync(db));
    }

    [PostgresFact]
    public async Task ARowWrittenWithoutTheNewColumns_GetsTheirDefaults()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var (hostOrg, property, supplierOrg) = await SeedParentsAsync(db);
        var id = Guid.NewGuid();

        // What the release before SP-04 writes: none of the new columns.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ServiceRequests" ("Id", "OrgId", "PropertyId", "SupplierOrgId", "RentalContext", "Category", "Urgency", "Notes", "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {hostOrg}, {property}, {supplierOrg}, 0, 'cleaning', 0, '', 0, false, now(), now());
            """);

        var stored = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal("[]", stored.OptionsJson);
        Assert.Equal("[]", stored.PriceLinesJson);
        Assert.Equal("[]", stored.WorkPhotosJson);
        Assert.False(stored.FinalAmountNeedsConfirmation);
        Assert.Null(stored.ScheduledStartUtc);
        Assert.Null(stored.ScheduledEndUtc);
        Assert.Null(stored.ServiceListingId);
        Assert.Null(stored.ResponseDueAt);
        Assert.Null(stored.CancelledBy);
        Assert.Null(stored.LastRemindedAt);
        Assert.Null(stored.ProposedStartUtc);
    }

    [PostgresFact]
    public async Task Checks_TheDatabaseRefusesATimeThatIsNotAnInterval()
    {
        var parents = await MigratedWithParentsAsync();
        var start = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ScheduledInterval", r => r.ScheduledStartUtc = start);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ScheduledInterval", r => r.ScheduledEndUtc = start);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ScheduledInterval", r => { r.ScheduledStartUtc = start; r.ScheduledEndUtc = start; });
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ScheduledInterval", r => { r.ScheduledStartUtc = start; r.ScheduledEndUtc = start.AddMinutes(-1); });

        // A valid interval, and none, are accepted.
        await SaveRequestAsync(parents, r => { r.ScheduledStartUtc = start; r.ScheduledEndUtc = start.AddMinutes(1); });
        await SaveRequestAsync(parents, _ => { });
    }

    [PostgresFact]
    public async Task Checks_TheDatabaseRefusesAProposalThatIsNotWhole()
    {
        var parents = await MigratedWithParentsAsync();
        var start = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        var at = new DateTime(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);

        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ProposedInterval", r => r.ProposedStartUtc = start);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ProposedInterval", r => r.ProposedAt = at);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ProposedInterval", r => { r.ProposedStartUtc = start; r.ProposedEndUtc = start.AddHours(2); });
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_ProposedInterval", r => { r.ProposedStartUtc = start; r.ProposedEndUtc = start; r.ProposedAt = at; });

        await SaveRequestAsync(parents, r => { r.ProposedStartUtc = start; r.ProposedEndUtc = start.AddHours(2); r.ProposedAt = at; });
    }

    [PostgresTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ServiceRequestLimits.MaxAmountCents + 1)]
    public async Task Checks_TheDatabaseRefusesAnAmountOutsideTheLimits(int cents)
    {
        var parents = await MigratedWithParentsAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_Amounts", r => r.EstimatedAmountCents = cents);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_Amounts", r => r.QuotedAmountCents = cents);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequests_Amounts", r => r.FinalAmountCents = cents);
    }

    [PostgresFact]
    public async Task Checks_TheLimitsOfTheAmountsAreAccepted()
    {
        var parents = await MigratedWithParentsAsync();

        await SaveRequestAsync(parents, r => { r.EstimatedAmountCents = 1; r.QuotedAmountCents = ServiceRequestLimits.MaxAmountCents; r.FinalAmountCents = 1; });
    }

    [PostgresFact]
    public async Task ServiceListing_DeletingTheServiceKeepsTheRequestAndClearsItsLink()
    {
        var parents = await MigratedWithParentsAsync();
        await using var db = _database!.CreateContext();
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = parents.SupplierOrg,
            Email = "migration-sp04@example.com",
            LegalName = "Migrazione Srl",
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = "[\"H501\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        var listing = new SupplierServiceListing
        {
            OrgId = parents.SupplierOrg,
            Slug = "pulizia-migrazione",
            Name = "Pulizia",
            Category = ServiceCategories.Cleaning,
            Status = SupplierServiceListingStatus.Active,
        };
        db.SupplierServiceListings.Add(listing);
        await db.SaveChangesAsync();
        var requestId = await SaveRequestAsync(parents, r =>
        {
            r.ServiceListingId = listing.Id;
            r.ServiceNameSnapshot = "Pulizia";
        });

        await db.Database.ExecuteSqlAsync($"DELETE FROM \"SupplierServiceListings\" WHERE \"Id\" = {listing.Id}");

        var stored = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Null(stored.ServiceListingId);
        // The name the request kept is what the supplier and the host still read.
        Assert.Equal("Pulizia", stored.ServiceNameSnapshot);
    }

    [PostgresFact]
    public async Task ServiceListing_ARequestCannotPointAtAServiceThatDoesNotExist()
    {
        var parents = await MigratedWithParentsAsync();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRequestAsync(parents, r => r.ServiceListingId = Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task Enums_AreStoredAsTheExplicitIntegersTheMobileAppAndTheMigrationsRelyOn()
    {
        var parents = await MigratedWithParentsAsync();
        var requestId = await SaveRequestAsync(parents, r =>
        {
            r.Status = ServiceRequestStatus.Annullato;
            r.CancelledBy = ServiceRequestActorParty.Supplier;
            r.CancelledAt = DateTime.UtcNow;
        });
        await using var db = _database!.CreateContext();

        var status = await db.Database.SqlQuery<int>($"SELECT \"Status\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"Id\" = {requestId}").SingleAsync();
        var cancelledBy = await db.Database.SqlQuery<int>($"SELECT \"CancelledBy\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"Id\" = {requestId}").SingleAsync();

        Assert.Equal(6, status); // Richiesto 0, PresoInCarico 1, InCorso 2, Completato 3, Pagato 4, Rifiutato 5, Annullato 6
        Assert.Equal(1, cancelledBy); // Host 0, Supplier 1, System 2
    }

    [PostgresFact]
    public async Task Index_TheRequestsOfASupplierAreIndexedByTheirTime()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        var definition = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc'")
            .SingleAsync();

        Assert.Contains("(\"SupplierOrgId\", \"ScheduledStartUtc\")", definition);
        Assert.DoesNotContain("UNIQUE", definition, StringComparison.OrdinalIgnoreCase);
    }

    // ─── helpers ───

    private sealed record Parents(Guid HostOrg, Guid Property, Guid SupplierOrg);

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddServiceRequestSchedule", StringComparison.Ordinal));
        Assert.True(index > 0, "AddServiceRequestSchedule migration not found.");
        return all[index - 1];
    }

    private static async Task<int> CountColumnsAsync(AppDbContext db)
    {
        var names = NewColumns;
        return (int)await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM information_schema.columns WHERE table_name = 'ServiceRequests' AND column_name = ANY({names})")
            .SingleAsync();
    }

    private static async Task<int> CountObjectsAsync(AppDbContext db, string catalog, string column, string name)
    {
        var sql = $"SELECT count(*) AS \"Value\" FROM {catalog} WHERE {column} = '{name}'";
        return (int)await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    private async Task<Parents> MigratedWithParentsAsync()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var (hostOrg, property, supplierOrg) = await SeedParentsAsync(db);
        return new Parents(hostOrg, property, supplierOrg);
    }

    private static async Task<(Guid HostOrg, Guid Property, Guid SupplierOrg)> SeedParentsAsync(AppDbContext db)
    {
        var hostOrg = new OrgEntity
        {
            Name = "Host",
            Slug = $"sp04-host-{Guid.NewGuid():N}"[..28],
            DisplayName = "Host",
            ContactEmail = "host-sp04@example.com",
            PlanTier = PlanTier.Starter,
        };
        var supplierOrg = new OrgEntity
        {
            Name = "Supplier",
            Slug = $"sp04-sup-{Guid.NewGuid():N}"[..28],
            DisplayName = "Supplier",
            ContactEmail = "supplier-sp04@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var property = new Property
        {
            OwnerId = "auth0|sp04-migration",
            OrgId = hostOrg.Id,
            Name = "Casa",
            Address = $"Via Migrazione {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        db.AddRange(hostOrg, supplierOrg, property);
        await db.SaveChangesAsync();
        return (hostOrg.Id, property.Id, supplierOrg.Id);
    }

    /// <summary>A request of the parents with the change applied, saved on a context of its own; returns its id.</summary>
    private async Task<Guid> SaveRequestAsync(Parents parents, Action<ServiceRequest> change)
    {
        await using var db = _database!.CreateContext();
        var request = new ServiceRequest
        {
            OrgId = parents.HostOrg,
            PropertyId = parents.Property,
            SupplierOrgId = parents.SupplierOrg,
            RentalContext = ServiceRequestRentalContext.LongRent,
            Category = ServiceCategories.Cleaning,
        };
        change(request);
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task AssertCheckViolationAsync(Parents parents, string constraint, Action<ServiceRequest> change)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRequestAsync(parents, change));
        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }
}
