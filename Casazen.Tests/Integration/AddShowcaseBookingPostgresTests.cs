using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
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
/// SP-10 migration (<see cref="AddShowcaseBooking"/>) on real PostgreSQL: it applies and reverts (and refuses to revert while a
/// request from a showcase exists, changing nothing), the requests of the hosts that exist before it keep their meaning, the
/// context check refuses the rows the booking never writes — a showcase request with a property or without a customer, a host's
/// request with a customer, a code or a place — the holds refuse a bad interval or expiry, the unique indexes say what is one
/// booking, the foreign keys cascade or clear as designed, and the new enum values are stored as the explicit integers the
/// mobile app and the migrations rely on.
/// </summary>
public class AddShowcaseBookingPostgresTests : IAsyncLifetime
{
    private static readonly string[] NewColumns =
    [
        "CustomerId", "LocationAccessNotes", "LocationAddress", "LocationCity", "LocationComuneIstat", "LocationFloor",
        "LocationPostalCode", "PublicCode", "ReminderSentAt", "Source",
    ];

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // ─── The migration ───────────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Migration_AppliesAndRevertsOnARealDatabase()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        Assert.Equal(NewColumns.Length, await CountColumnsAsync(db));
        Assert.Equal(2, await CountTablesAsync(db));
        Assert.Equal("YES", await IsNullableAsync(db, "PropertyId"));
        Assert.Equal(1, await CountObjectsAsync(db, "pg_constraint", "conname", "CK_ServiceRequests_Context"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        Assert.Equal(0, await CountColumnsAsync(db));
        Assert.Equal(0, await CountTablesAsync(db));
        Assert.Equal("NO", await IsNullableAsync(db, "PropertyId"));
        Assert.Equal(0, await CountObjectsAsync(db, "pg_constraint", "conname", "CK_ServiceRequests_Context"));
        Assert.Equal(0, await CountObjectsAsync(db, "pg_indexes", "indexname", "UIX_ServiceRequests_SupplierOrgId_PublicCode"));

        await db.Database.MigrateAsync();
        Assert.Equal(NewColumns.Length, await CountColumnsAsync(db));
        Assert.Equal(2, await CountTablesAsync(db));
    }

    [PostgresFact]
    public async Task Migration_RevertRefusesWhileAShowcaseRequestExists_AndChangesNothing()
    {
        var parents = await MigratedWithParentsAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, "revert@example.com");
        var requestId = await SaveRequestAsync(Showcase(parents, customer));
        await using var db = _database!.CreateContext();

        // The request has no property: putting the column back to NOT NULL would invent one, so the revert stops (a foreign key
        // violation) and the transaction of the migration undoes the part of it that already ran.
        await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db)));

        await using var after = _database!.CreateContext();
        Assert.Equal(NewColumns.Length, await CountColumnsAsync(after));
        Assert.Equal(2, await CountTablesAsync(after));
        Assert.Equal(1, await after.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.Id == requestId));
        Assert.Equal(1, await after.ServiceCustomers.CountAsync(c => c.Id == customer));
    }

    [PostgresFact]
    public async Task Migration_TheRequestsOfTheHostsThatExistBefore_KeepTheirMeaning()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var (hostOrg, property, supplierOrg) = await SeedHostParentsAsync(db);
        var shortRent = Guid.NewGuid();
        var longRent = Guid.NewGuid();

        // What the release before SP-10 writes: a short-rent request with no booking, a long-rent one, none of the new columns.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ServiceRequests" ("Id", "OrgId", "PropertyId", "SupplierOrgId", "RentalContext", "Category", "Urgency", "Notes", "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
            VALUES ({shortRent}, {hostOrg}, {property}, {supplierOrg}, 0, 'cleaning', 0, '', 0, false, now(), now()),
                   ({longRent}, {hostOrg}, {property}, {supplierOrg}, 1, 'cleaning', 0, '', 0, false, now(), now());
            """);

        await db.Database.MigrateAsync();

        var stored = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.Id == shortRent || r.Id == longRent).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, request =>
        {
            Assert.Equal(ServiceRequestSource.Host, request.Source);
            Assert.Equal(property, request.PropertyId);
            Assert.Null(request.CustomerId);
            Assert.Null(request.PublicCode);
            Assert.Null(request.LocationCity);
            Assert.Null(request.ReminderSentAt);
        });
        Assert.Contains(stored, r => r.RentalContext == ServiceRequestRentalContext.ShortRent);
        Assert.Contains(stored, r => r.RentalContext == ServiceRequestRentalContext.LongRent);
    }

    [PostgresFact]
    public async Task ARowWrittenWithoutTheNewColumns_AfterTheMigration_StillSatisfiesTheContextCheck()
    {
        var parents = await MigratedWithParentsAsync();
        await using var db = _database!.CreateContext();
        var id = Guid.NewGuid();

        // The previous release while the new one is rolling out writes none of the new columns: the defaults make it a host's request.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ServiceRequests" ("Id", "OrgId", "PropertyId", "SupplierOrgId", "RentalContext", "Category", "Urgency", "Notes", "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {parents.HostOrg}, {parents.Property}, {parents.SupplierOrg}, 0, 'cleaning', 0, '', 0, false, now(), now());
            """);

        var stored = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(ServiceRequestSource.Host, stored.Source);
        Assert.Null(stored.CustomerId);
    }

    // ─── The context check ───────────────────────────────────────────────────────────────────────────────────────────

    [PostgresTheory]
    [InlineData("property")]
    [InlineData("booking")]
    [InlineData("no-customer")]
    [InlineData("no-code")]
    [InlineData("no-city")]
    [InlineData("host-source")]
    public async Task Context_AShowcaseRequestThatIsNotWhole_IsRefused(string flaw)
    {
        var parents = await MigratedWithParentsAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, $"{flaw}@example.com");

        await AssertCheckViolationAsync("CK_ServiceRequests_Context", Showcase(parents, customer, request =>
        {
            switch (flaw)
            {
                case "property": request.PropertyId = parents.Property; break;
                case "booking": request.BookingId = Guid.NewGuid(); break;
                case "no-customer": request.CustomerId = null; break;
                case "no-code": request.PublicCode = null; break;
                case "no-city": request.LocationCity = null; break;
                case "host-source": request.Source = ServiceRequestSource.Host; break;
            }
        }));
    }

    [PostgresTheory]
    [InlineData("customer")]
    [InlineData("code")]
    [InlineData("city")]
    [InlineData("istat")]
    [InlineData("postal-code")]
    [InlineData("address")]
    [InlineData("floor")]
    [InlineData("access-notes")]
    [InlineData("showcase-source")]
    [InlineData("no-property")]
    public async Task Context_AHostsRequestThatCarriesTheShowcase_IsRefused(string flaw)
    {
        var parents = await MigratedWithParentsAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, $"{flaw}@example.com");

        await AssertCheckViolationAsync("CK_ServiceRequests_Context", Host(parents, request =>
        {
            switch (flaw)
            {
                case "customer": request.CustomerId = customer; break;
                case "code": request.PublicCode = "ABCDEFGHJK"; break;
                case "city": request.LocationCity = "Monza"; break;
                case "istat": request.LocationComuneIstat = "015146"; break;
                case "postal-code": request.LocationPostalCode = "20900"; break;
                case "address": request.LocationAddress = "Via Segretissima 7"; break;
                case "floor": request.LocationFloor = "Piano 3"; break;
                case "access-notes": request.LocationAccessNotes = "Citofono"; break;
                case "showcase-source": request.Source = ServiceRequestSource.Showcase; break;
                case "no-property": request.PropertyId = null; break;
            }
        }));
    }

    [PostgresFact]
    public async Task Context_AContextThatDoesNotExist_IsRefused()
    {
        var parents = await MigratedWithParentsAsync();

        await AssertCheckViolationAsync("CK_ServiceRequests_Context", Host(parents, request => request.RentalContext = (ServiceRequestRentalContext)3));
    }

    [PostgresFact]
    public async Task Context_TheRowsTheRulesWrite_AreAccepted()
    {
        var parents = await MigratedWithParentsAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, "legale@example.com");

        // A showcase request with everything and with only what it needs; a host's short-rent request without a booking (older
        // requests have none) and a long-rent one.
        await SaveRequestAsync(Showcase(parents, customer, r =>
        {
            r.LocationComuneIstat = "015146";
            r.LocationPostalCode = "20900";
            r.LocationAddress = "ciphertext-of-the-street";
            r.LocationFloor = "ciphertext-of-the-floor";
            r.LocationAccessNotes = "ciphertext-of-the-notes";
        }));
        await SaveRequestAsync(Showcase(parents, customer));
        await SaveRequestAsync(Host(parents, r => r.RentalContext = ServiceRequestRentalContext.ShortRent));
        await SaveRequestAsync(Host(parents, _ => { }));
    }

    // ─── The holds ───────────────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Holds_TheChecksRefuseAnIntervalThatIsNotOne_AndAnExpiryBeforeTheCreation()
    {
        var parents = await MigratedWithParentsAsync();
        var start = new DateTime(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc);

        await AssertViolationAsync(
            PostgresErrorCodes.CheckViolation, "CK_ShowcaseBookingHolds_Interval",
            Hold(parents.SupplierOrg, h => { h.StartUtc = start; h.EndUtc = start; }));
        await AssertViolationAsync(
            PostgresErrorCodes.CheckViolation, "CK_ShowcaseBookingHolds_Interval",
            Hold(parents.SupplierOrg, h => { h.StartUtc = start; h.EndUtc = start.AddMinutes(-1); }));
        await AssertViolationAsync(
            PostgresErrorCodes.CheckViolation, "CK_ShowcaseBookingHolds_Expiry",
            Hold(parents.SupplierOrg, h => { h.CreatedAt = created; h.ExpiresAt = created; }));
        await AssertViolationAsync(
            PostgresErrorCodes.CheckViolation, "CK_ShowcaseBookingHolds_Expiry",
            Hold(parents.SupplierOrg, h => { h.CreatedAt = created; h.ExpiresAt = created.AddSeconds(-1); }));

        // A valid hold is accepted, with and without a payload.
        await SaveAsync(Hold(parents.SupplierOrg, h => h.Payload = "ciphertext"));
        await SaveAsync(Hold(parents.SupplierOrg, h => h.Payload = null));
    }

    [PostgresFact]
    public async Task Holds_TheSameClientRequestIdOrCodeOfOneSupplier_IsOneBooking_AnotherSupplierMayRepeatThem()
    {
        var parents = await MigratedWithParentsAsync();
        var other = await SeedSupplierAsync();
        var clientRequestId = Guid.NewGuid();
        await SaveAsync(Hold(parents.SupplierOrg, h => { h.ClientRequestId = clientRequestId; h.PublicCode = "AAAAAAAAAA"; }));

        await AssertViolationAsync(
            PostgresErrorCodes.UniqueViolation, "UIX_ShowcaseBookingHolds_OrgId_ClientRequestId",
            Hold(parents.SupplierOrg, h => h.ClientRequestId = clientRequestId));
        await AssertViolationAsync(
            PostgresErrorCodes.UniqueViolation, "UIX_ShowcaseBookingHolds_OrgId_PublicCode",
            Hold(parents.SupplierOrg, h => h.PublicCode = "AAAAAAAAAA"));
        await SaveAsync(Hold(other, h => { h.ClientRequestId = clientRequestId; h.PublicCode = "AAAAAAAAAA"; }));
    }

    // ─── The customers and the codes ─────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Customers_OneAddressIsOneCustomerPerSupplier_AndTheCodeOfARequestIsUniqueForItsSupplier()
    {
        var parents = await MigratedWithParentsAsync();
        var other = await SeedSupplierAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, "unico@example.com");

        await AssertViolationAsync(
            PostgresErrorCodes.UniqueViolation, "UIX_ServiceCustomers_OrgId_EmailHash",
            NewCustomer(parents.SupplierOrg, "unico@example.com"));
        await SaveCustomerAsync(other, "unico@example.com");

        await SaveRequestAsync(Showcase(parents, customer, r => r.PublicCode = "BBBBBBBBBB"));
        await AssertViolationAsync(
            PostgresErrorCodes.UniqueViolation, "UIX_ServiceRequests_SupplierOrgId_PublicCode",
            Showcase(parents, customer, r => r.PublicCode = "BBBBBBBBBB"));
        var otherCustomer = await SaveCustomerAsync(other, "altro@example.com");
        await SaveRequestAsync(Showcase(parents with { SupplierOrg = other }, otherCustomer, r => r.PublicCode = "BBBBBBBBBB"));
    }

    // ─── The foreign keys ────────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task ForeignKeys_DeletingTheSupplierProfile_DeletesItsCustomersAndHolds()
    {
        var parents = await MigratedWithParentsAsync();
        var supplier = await SeedSupplierAsync();
        await SaveCustomerAsync(supplier, "figlio@example.com");
        await SaveAsync(Hold(supplier, _ => { }));
        await using var db = _database!.CreateContext();

        await db.Database.ExecuteSqlAsync($"DELETE FROM \"SupplierProfiles\" WHERE \"OrgId\" = {supplier}");

        Assert.Equal(0, await db.ServiceCustomers.CountAsync(c => c.OrgId == supplier));
        Assert.Equal(0, await db.ShowcaseBookingHolds.CountAsync(h => h.OrgId == supplier));
        Assert.Equal(1, await db.SupplierProfiles.CountAsync(sp => sp.OrgId == parents.SupplierOrg));
    }

    [PostgresFact]
    public async Task ForeignKeys_ACustomerWithARequestCannotBeDeleted_TheRequestsHoldRemembersItsRequestOnly()
    {
        var parents = await MigratedWithParentsAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, "protetto@example.com");
        var requestId = await SaveRequestAsync(Showcase(parents, customer));
        await SaveAsync(Hold(parents.SupplierOrg, h => h.ServiceRequestId = requestId));
        await using var db = _database!.CreateContext();

        var refused = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlAsync($"DELETE FROM \"ServiceCustomers\" WHERE \"Id\" = {customer}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);

        // Deleting the request clears the link of its hold and keeps the hold.
        await db.Database.ExecuteSqlAsync($"DELETE FROM \"ServiceRequests\" WHERE \"Id\" = {requestId}");
        var hold = await db.ShowcaseBookingHolds.AsNoTracking().SingleAsync(h => h.OrgId == parents.SupplierOrg);
        Assert.Null(hold.ServiceRequestId);
        await db.Database.ExecuteSqlAsync($"DELETE FROM \"ServiceCustomers\" WHERE \"Id\" = {customer}");
    }

    // ─── The integers ────────────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Enums_TheNewValuesAreStoredAsTheExplicitIntegersTheMobileAppAndTheMigrationsRelyOn()
    {
        var parents = await MigratedWithParentsAsync();
        var customer = await SaveCustomerAsync(parents.SupplierOrg, "interi@example.com");
        var requestId = await SaveRequestAsync(Showcase(parents, customer, r =>
        {
            r.Status = ServiceRequestStatus.Annullato;
            r.CancelledBy = ServiceRequestActorParty.Customer;
            r.CancelledAt = DateTime.UtcNow;
        }));
        await using var db = _database!.CreateContext();

        var context = await db.Database.SqlQuery<int>($"SELECT \"RentalContext\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"Id\" = {requestId}").SingleAsync();
        var source = await db.Database.SqlQuery<int>($"SELECT \"Source\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"Id\" = {requestId}").SingleAsync();
        var cancelledBy = await db.Database.SqlQuery<int>($"SELECT \"CancelledBy\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"Id\" = {requestId}").SingleAsync();

        Assert.Equal(2, context); // ShortRent 0, LongRent 1, Showcase 2
        Assert.Equal(1, source); // Host 0, Showcase 1
        Assert.Equal(3, cancelledBy); // Host 0, Supplier 1, System 2, Customer 3
    }

    [PostgresFact]
    public async Task Index_TheRemindersAndTheCustomerAreIndexed()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        var customer = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_ServiceRequests_CustomerId'")
            .SingleAsync();
        var code = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'UIX_ServiceRequests_SupplierOrgId_PublicCode'")
            .SingleAsync();
        var expiry = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_ShowcaseBookingHolds_ExpiresAt'")
            .SingleAsync();

        Assert.Contains("(\"CustomerId\")", customer);
        Assert.Contains("UNIQUE", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(\"SupplierOrgId\", \"PublicCode\")", code);
        // Host requests have no code: the unique index covers only the rows that have one.
        Assert.Contains("WHERE", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(\"ExpiresAt\")", expiry);
    }

    // ─── helpers ───

    private sealed record Parents(Guid HostOrg, Guid Property, Guid SupplierOrg);

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddShowcaseBooking", StringComparison.Ordinal));
        Assert.True(index > 0, "AddShowcaseBooking migration not found.");
        return all[index - 1];
    }

    private static async Task<int> CountColumnsAsync(AppDbContext db)
    {
        var names = NewColumns;
        return (int)await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM information_schema.columns WHERE table_name = 'ServiceRequests' AND column_name = ANY({names})")
            .SingleAsync();
    }

    private static async Task<int> CountTablesAsync(AppDbContext db) =>
        (int)await db.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM information_schema.tables WHERE table_name IN ('ServiceCustomers', 'ShowcaseBookingHolds')")
            .SingleAsync();

    private static async Task<string> IsNullableAsync(AppDbContext db, string column) =>
        await db.Database
            .SqlQuery<string>($"SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'ServiceRequests' AND column_name = {column}")
            .SingleAsync();

    private static async Task<int> CountObjectsAsync(AppDbContext db, string catalog, string column, string name)
    {
        var sql = $"SELECT count(*) AS \"Value\" FROM {catalog} WHERE {column} = '{name}'";
        return (int)await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    private async Task<Parents> MigratedWithParentsAsync()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var (hostOrg, property, supplierOrg) = await SeedHostParentsAsync(db);
        return new Parents(hostOrg, property, supplierOrg);
    }

    /// <summary>A host org with a property and a supplier org with an active profile.</summary>
    private static async Task<(Guid HostOrg, Guid Property, Guid SupplierOrg)> SeedHostParentsAsync(AppDbContext db)
    {
        var hostOrg = new OrgEntity
        {
            Name = "Host",
            Slug = $"sp10-host-{Guid.NewGuid():N}"[..28],
            DisplayName = "Host",
            ContactEmail = "host-sp10@example.com",
            PlanTier = PlanTier.Starter,
        };
        var supplierOrg = NewSupplierOrg();
        var property = new Property
        {
            OwnerId = "auth0|sp10-migration",
            OrgId = hostOrg.Id,
            Name = "Casa",
            Address = $"Via Migrazione {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            IsActive = true,
        };
        db.AddRange(hostOrg, supplierOrg);
        await db.SaveChangesAsync();

        // Plain SQL for the rows that have columns added by later migrations: the first test runs at the schema before this
        // migration, and saving the entities through the model would write the columns that do not exist there yet (42703). The
        // property uses LegacyPropertyRows (AM-03: ResponsibleUserId; Wave 3: CancellationFullRefundHours); the profile, its
        // own statement (SP-15a: the supplier payments columns). The parents need no CIN.
        await LegacyPropertyRows.InsertAsync(db, property);
        await SupplierProfileSql.InsertAsync(db, NewSupplierProfile(supplierOrg.Id));
        return (hostOrg.Id, property.Id, supplierOrg.Id);
    }

    private async Task<Guid> SeedSupplierAsync()
    {
        await using var db = _database!.CreateContext();
        var org = NewSupplierOrg();
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(NewSupplierProfile(org.Id));
        await db.SaveChangesAsync();
        return org.Id;
    }

    private static OrgEntity NewSupplierOrg() => new()
    {
        Name = "Supplier",
        Slug = $"sp10-sup-{Guid.NewGuid():N}"[..28],
        DisplayName = "Supplier",
        ContactEmail = "supplier-sp10@example.com",
        OrgType = OrgType.Supplier,
        PlanTier = PlanTier.Starter,
    };

    private static SupplierProfile NewSupplierProfile(Guid orgId) => new()
    {
        OrgId = orgId,
        Email = $"sp10-{Guid.NewGuid():N}@example.com",
        LegalName = "Fornitore SP-10 Srl",
        Phone = "+39 06 101010",
        Status = SupplierStatus.Active,
        ComuniJson = "[\"H501\"]",
        CategoriesJson = "[\"cleaning\"]",
    };

    private static ServiceCustomer NewCustomer(Guid supplierOrg, string email) => new()
    {
        OrgId = supplierOrg,
        // Any 64 hexadecimal characters stand for the HMAC of the address; the same address gives the same text.
        EmailHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email))).ToLowerInvariant(),
        FullName = "Mario Rossi",
        Email = email,
        Phone = "+393331234567",
        Locale = "it",
        PrivacyNoticeVersion = "2026-11-test",
        PrivacyAcceptedAt = new DateTime(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc),
        ConsentIp = "203.0.113.7",
        CreatedAt = new DateTime(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc),
    };

    private async Task<Guid> SaveCustomerAsync(Guid supplierOrg, string email)
    {
        var customer = NewCustomer(supplierOrg, email);
        await SaveAsync(customer);
        return customer.Id;
    }

    private static ShowcaseBookingHold Hold(Guid supplierOrg, Action<ShowcaseBookingHold> change)
    {
        var start = new DateTime(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc);
        var hold = new ShowcaseBookingHold
        {
            OrgId = supplierOrg,
            ClientRequestId = Guid.NewGuid(),
            StartUtc = start,
            EndUtc = start.AddHours(2),
            PublicCode = BookingCodes.New(),
            TokenHash = new string('a', 64),
            EmailHash = new string('b', 64),
            ExpiresAt = created.AddMinutes(30),
            CreatedAt = created,
        };
        change(hold);
        return hold;
    }

    private static ServiceRequest Showcase(Parents parents, Guid customerId, Action<ServiceRequest>? change = null)
    {
        var request = new ServiceRequest
        {
            OrgId = parents.SupplierOrg,
            SupplierOrgId = parents.SupplierOrg,
            RentalContext = ServiceRequestRentalContext.Showcase,
            Source = ServiceRequestSource.Showcase,
            PropertyId = null,
            BookingId = null,
            Category = ServiceCategories.Cleaning,
            CustomerId = customerId,
            PublicCode = BookingCodes.New(),
            LocationCity = "Monza",
        };
        change?.Invoke(request);
        return request;
    }

    private static ServiceRequest Host(Parents parents, Action<ServiceRequest> change)
    {
        var request = new ServiceRequest
        {
            OrgId = parents.HostOrg,
            PropertyId = parents.Property,
            SupplierOrgId = parents.SupplierOrg,
            RentalContext = ServiceRequestRentalContext.LongRent,
            Category = ServiceCategories.Cleaning,
        };
        change(request);
        return request;
    }

    /// <summary>The row saved on a context of its own: a failure leaves nothing tracked behind.</summary>
    private async Task SaveAsync(object row)
    {
        await using var db = _database!.CreateContext();
        db.Add(row);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SaveRequestAsync(ServiceRequest request)
    {
        await SaveAsync(request);
        return request.Id;
    }

    private async Task AssertCheckViolationAsync(string constraint, ServiceRequest request) =>
        await AssertViolationAsync(PostgresErrorCodes.CheckViolation, constraint, request);

    private async Task AssertViolationAsync(string sqlState, string constraint, object row)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(row));
        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(sqlState, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }
}
