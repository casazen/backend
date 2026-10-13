using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15a migration (<c>AddSupplierPayments</c>) on real PostgreSQL: it applies and reverts; a request written by code that does
/// not know the new columns (the previous release while the migration is applied) still gets <c>PaymentMode = Manual</c>; the checks
/// refuse the rows the rules never write (a commission not strictly below the amount, a net that is not what is left, a percentage
/// outside 0 to 50, a host payment without a payer org, a paid payment that does not say when and how, a refund above the amount);
/// the unique index lets a request have one payment that is not canceled; nothing under a payment can be deleted; and the enums are
/// stored as the explicit integers the code and the webhook of SP-15b rely on.
/// </summary>
public class AddSupplierPaymentsPostgresTests : IAsyncLifetime
{
    private static readonly string[] PaymentColumns =
    [
        "Id", "ServiceRequestId", "SupplierOrgId", "PayerKind", "PayerOrgId", "AmountCents", "Currency", "CommissionPercent",
        "ApplicationFeeCents", "NetCents", "FeeVatMode", "FeeVatCents", "Status", "StripePaymentIntentId", "ConnectedAccountId",
        "PaymentIntentCount", "PaymentTokenHash", "RequestedAt", "LastSentAt", "SentCount", "LateAt", "FailureCode", "PaidAt",
        "PaidVia", "RefundedCents", "LineItemsJson", "OfflineNote", "MarkedPaidByUserId", "CanceledAt", "CreatedAt", "UpdatedAt",
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
        Assert.Equal(PaymentColumns.Length, await CountColumnsAsync(db, "ServiceRequestPayments"));
        Assert.Equal(3, await CountColumnsAsync(db, "ServiceRequests", "PaymentMode", "FinalAmountConfirmedAt", "PaidBy"));
        Assert.Equal(1, await CountColumnsAsync(db, "SupplierProfiles", "CommissionPercentOverride"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        Assert.Equal(0, await CountColumnsAsync(db, "ServiceRequestPayments"));
        Assert.Equal(0, await CountColumnsAsync(db, "ServiceRequests", "PaymentMode", "FinalAmountConfirmedAt", "PaidBy"));
        Assert.Equal(0, await CountColumnsAsync(db, "SupplierProfiles", "CommissionPercentOverride"));
        Assert.Equal(0, await CountObjectsAsync(db, "pg_constraint", "conname", "CK_SupplierProfiles_CommissionPercentOverride"));

        await db.Database.MigrateAsync();
        Assert.Equal(PaymentColumns.Length, await CountColumnsAsync(db, "ServiceRequestPayments"));
    }

    [PostgresFact]
    public async Task ARequestWrittenWithoutTheNewColumns_IsManual()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var parents = await SeedParentsAsync(db);
        var id = Guid.NewGuid();

        // What the release before SP-15a writes: none of the new columns.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ServiceRequests" ("Id", "OrgId", "PropertyId", "SupplierOrgId", "RentalContext", "Category", "Urgency", "Notes", "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {parents.HostOrg}, {parents.Property}, {parents.SupplierOrg}, 0, 'cleaning', 0, '', 0, false, now(), now());
            """);

        var stored = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(ServiceRequestPaymentMode.Manual, stored.PaymentMode);
        Assert.Null(stored.FinalAmountConfirmedAt);
        var stored0 = await db.Database.SqlQuery<int>($"SELECT \"PaymentMode\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"Id\" = {id}").SingleAsync();
        Assert.Equal(0, stored0);
    }

    [PostgresFact]
    public async Task APaymentWrittenWithTheMinimum_GetsTheDefaults()
    {
        var parents = await MigratedWithRequestAsync();
        await using var db = _database!.CreateContext();
        var id = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ServiceRequestPayments" ("Id", "ServiceRequestId", "SupplierOrgId", "PayerKind", "PayerOrgId", "AmountCents", "Currency",
                "CommissionPercent", "ApplicationFeeCents", "NetCents", "Status", "PaymentIntentCount", "SentCount", "RefundedCents", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {parents.Request}, {parents.SupplierOrg}, 0, {parents.HostOrg}, 6000, 'eur', 10, 600, 5400, 0, 0, 0, 0, now(), now());
            """);

        var stored = await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal("[]", stored.LineItemsJson);
        Assert.Null(stored.FeeVatMode);
        Assert.Null(stored.FeeVatCents);
        Assert.Null(stored.PaidVia);
        Assert.Null(stored.StripePaymentIntentId);
        Assert.Equal(10m, stored.CommissionPercent);
    }

    // ─── The checks ───

    [PostgresTheory]
    [InlineData(0, 0, 0)] // no amount
    [InlineData(-5, 0, -5)]
    [InlineData(ServiceRequestLimits.MaxAmountCents + 1, 0, ServiceRequestLimits.MaxAmountCents + 1)] // above the bound of the catalog
    [InlineData(6_000, 6_000, 0)] // a commission of the whole price: Stripe wants it strictly below the amount
    [InlineData(6_000, 7_000, -1_000)]
    [InlineData(6_000, -1, 6_001)] // a negative commission
    [InlineData(6_000, 600, 5_000)] // the net is not what is left
    [InlineData(6_000, 600, 6_000)]
    public async Task Checks_TheAmountsMustBeCoherent(int amount, int fee, int net)
    {
        var parents = await MigratedWithRequestAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Amounts", p =>
        {
            p.AmountCents = amount;
            p.ApplicationFeeCents = fee;
            p.NetCents = net;
        });
    }

    [PostgresFact]
    public async Task Checks_ARefundCannotExceedTheAmount_AndTheCountersCannotBeNegative()
    {
        var parents = await MigratedWithRequestAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Amounts", p => p.RefundedCents = 6_001);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Amounts", p => p.RefundedCents = -1);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Amounts", p => p.PaymentIntentCount = -1);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Amounts", p => p.SentCount = -1);
        // The whole amount can be refunded.
        await SavePaymentAsync(parents, p =>
        {
            p.Status = ServicePaymentStatus.Refunded;
            p.PaidAt = DateTime.UtcNow;
            p.PaidVia = ServicePaymentChannel.Stripe;
            p.RefundedCents = 6_000;
        });
    }

    [PostgresTheory]
    [InlineData(-0.01)]
    [InlineData(50.01)]
    [InlineData(100)]
    public async Task Checks_TheCommissionPercentageIsFromZeroToFifty(double percent)
    {
        var parents = await MigratedWithRequestAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_CommissionPercent", p => p.CommissionPercent = (decimal)percent);
    }

    [PostgresTheory]
    [InlineData(0)]
    [InlineData(7.5)]
    [InlineData(50)]
    public async Task Checks_TheLimitsOfTheCommissionPercentageAreAccepted(double percent)
    {
        var parents = await MigratedWithRequestAsync();

        await SavePaymentAsync(parents, p =>
        {
            p.CommissionPercent = (decimal)percent;
            p.ApplicationFeeCents = 0;
            p.NetCents = p.AmountCents;
        });
    }

    [PostgresFact]
    public async Task Checks_TheVatOnTheCommissionIsEmptyOrWhole()
    {
        var parents = await MigratedWithRequestAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_FeeVat", p => p.FeeVatMode = ServiceFeeVatMode.Included);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_FeeVat", p => p.FeeVatCents = 132);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_FeeVat", p => { p.FeeVatMode = ServiceFeeVatMode.Added; p.FeeVatCents = -1; });
        await SavePaymentAsync(parents, p => { p.FeeVatMode = ServiceFeeVatMode.Added; p.FeeVatCents = 132; });
    }

    [PostgresFact]
    public async Task Checks_AHostPaymentNeedsItsPayerOrg_APrivateCustomerHasNone()
    {
        var parents = await MigratedWithRequestAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Payer", p => { p.PayerKind = ServicePayerKind.Host; p.PayerOrgId = null; });
        await SavePaymentAsync(parents, p => { p.PayerKind = ServicePayerKind.Private; p.PayerOrgId = null; });
    }

    [PostgresTheory]
    [InlineData(ServicePaymentStatus.Paid)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded)]
    [InlineData(ServicePaymentStatus.Refunded)]
    public async Task Checks_APaymentThatWasPaidSaysWhenAndHow(ServicePaymentStatus status)
    {
        var parents = await MigratedWithRequestAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Paid", p => p.Status = status);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Paid", p => { p.Status = status; p.PaidAt = DateTime.UtcNow; });
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPayments_Paid", p => { p.Status = status; p.PaidVia = ServicePaymentChannel.Stripe; });
        await SavePaymentAsync(parents, p => { p.Status = status; p.PaidAt = DateTime.UtcNow; p.PaidVia = ServicePaymentChannel.Stripe; });
    }

    [PostgresTheory]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task Checks_TheCommissionOverrideOfASupplierIsFromZeroToFifty(int percent)
    {
        var parents = await MigratedWithRequestAsync();
        await using var db = _database!.CreateContext();
        db.SupplierProfiles.Add(NewProfile(parents.SupplierOrg, percent));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_SupplierProfiles_CommissionPercentOverride", postgres.ConstraintName);
    }

    [PostgresTheory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(50)]
    public async Task Checks_TheCommissionOverrideLimitsAreAccepted(int? percent)
    {
        var parents = await MigratedWithRequestAsync();
        await using var db = _database!.CreateContext();
        db.SupplierProfiles.Add(NewProfile(parents.SupplierOrg, percent));
        await db.SaveChangesAsync();

        var stored = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == parents.SupplierOrg);
        Assert.Equal((decimal?)percent, stored.CommissionPercentOverride);
    }

    // ─── Keys, indexes, enums ───

    [PostgresFact]
    public async Task Index_OnePaymentPerRequestThatIsNotCanceled()
    {
        var parents = await MigratedWithRequestAsync();
        await SavePaymentAsync(parents, p => p.Status = ServicePaymentStatus.Canceled);
        await SavePaymentAsync(parents, p => p.Status = ServicePaymentStatus.Canceled);
        await SavePaymentAsync(parents, p => p.Status = ServicePaymentStatus.Requested);

        // The request has a live payment now: every other status is refused, only a canceled row is accepted.
        foreach (var status in new[] { ServicePaymentStatus.Requested, ServicePaymentStatus.Processing, ServicePaymentStatus.Failed, ServicePaymentStatus.NeedsReview })
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SavePaymentAsync(parents, p => p.Status = status));
            var postgres = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("UIX_ServiceRequestPayments_ServiceRequestId_Live", postgres.ConstraintName);
        }

        await SavePaymentAsync(parents, p => p.Status = ServicePaymentStatus.Canceled);
        await using var db = _database!.CreateContext();
        Assert.Equal(4, await db.ServiceRequestPayments.CountAsync(p => p.ServiceRequestId == parents.Request));
        var definition = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'UIX_ServiceRequestPayments_ServiceRequestId_Live'")
            .SingleAsync();
        Assert.Contains("UNIQUE", definition);
        Assert.Contains("WHERE", definition);
        Assert.Contains("<> 4", definition);
    }

    [PostgresFact]
    public async Task Index_APaymentIntentBelongsToOnePayment_ManyPaymentsMayHaveNone()
    {
        var parents = await MigratedWithRequestAsync();
        await SavePaymentAsync(parents, p => { p.Status = ServicePaymentStatus.Canceled; p.StripePaymentIntentId = "pi_one"; });
        await SavePaymentAsync(parents, p => { p.Status = ServicePaymentStatus.Canceled; p.StripePaymentIntentId = null; });
        await SavePaymentAsync(parents, p => { p.Status = ServicePaymentStatus.Canceled; p.StripePaymentIntentId = null; });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SavePaymentAsync(parents, p => { p.Status = ServicePaymentStatus.Canceled; p.StripePaymentIntentId = "pi_one"; }));

        Assert.Equal("UIX_ServiceRequestPayments_StripePaymentIntentId", Assert.IsType<PostgresException>(ex.InnerException).ConstraintName);
    }

    [PostgresFact]
    public async Task ForeignKeys_NothingUnderAPaymentCanBeDeleted()
    {
        var parents = await MigratedWithRequestAsync();
        await SavePaymentAsync(parents, p => p.Status = ServicePaymentStatus.Requested);
        await using var db = _database!.CreateContext();

        foreach (var sql in new[]
                 {
                     $"DELETE FROM \"ServiceRequests\" WHERE \"Id\" = '{parents.Request}'",
                     $"DELETE FROM \"Orgs\" WHERE \"Id\" = '{parents.SupplierOrg}'",
                     $"DELETE FROM \"Orgs\" WHERE \"Id\" = '{parents.HostOrg}'",
                 })
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
            var postgres = ex as PostgresException ?? ex.InnerException as PostgresException;
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres?.SqlState);
        }
    }

    [PostgresFact]
    public async Task ForeignKeys_APaymentCannotPointAtARequestThatDoesNotExist()
    {
        var parents = await MigratedWithRequestAsync();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SavePaymentAsync(parents with { Request = Guid.NewGuid() }, _ => { }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task Enums_AreStoredAsTheExplicitIntegersTheCodeAndTheWebhookRelyOn()
    {
        var parents = await MigratedWithRequestAsync();
        var id = await SavePaymentAsync(parents, p =>
        {
            p.Status = ServicePaymentStatus.NeedsReview;
            p.PayerKind = ServicePayerKind.Private;
            p.PayerOrgId = null;
            p.PaidVia = ServicePaymentChannel.Offline;
            p.FeeVatMode = ServiceFeeVatMode.Added;
            p.FeeVatCents = 100;
        });
        await using var db = _database!.CreateContext();

        var status = await db.Database.SqlQuery<int>($"SELECT \"Status\" AS \"Value\" FROM \"ServiceRequestPayments\" WHERE \"Id\" = {id}").SingleAsync();
        var payerKind = await db.Database.SqlQuery<int>($"SELECT \"PayerKind\" AS \"Value\" FROM \"ServiceRequestPayments\" WHERE \"Id\" = {id}").SingleAsync();
        var paidVia = await db.Database.SqlQuery<int>($"SELECT \"PaidVia\" AS \"Value\" FROM \"ServiceRequestPayments\" WHERE \"Id\" = {id}").SingleAsync();
        var feeVatMode = await db.Database.SqlQuery<int>($"SELECT \"FeeVatMode\" AS \"Value\" FROM \"ServiceRequestPayments\" WHERE \"Id\" = {id}").SingleAsync();

        // Requested 0, Processing 1, Paid 2, Failed 3, Canceled 4, PartiallyRefunded 5, Refunded 6, NeedsReview 7.
        Assert.Equal(7, status);
        Assert.Equal(1, payerKind); // Host 0, Private 1
        Assert.Equal(1, paidVia); // Stripe 0, Offline 1
        Assert.Equal(2, feeVatMode); // Included 1, Added 2
        Assert.Equal(1, (int)ServiceRequestPaymentMode.Online); // Manual 0
        Assert.Equal(4, (int)ServicePaymentStatus.Canceled);
    }

    [PostgresFact]
    public async Task RequestAndPaymentMode_AreStoredAsOnline1_AndTheConfirmationKeepsItsInstant()
    {
        var parents = await MigratedWithRequestAsync();
        await using var db = _database!.CreateContext();
        var confirmedAt = new DateTime(2026, 10, 9, 8, 30, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlAsync($"UPDATE \"ServiceRequests\" SET \"PaymentMode\" = 1, \"FinalAmountConfirmedAt\" = {confirmedAt} WHERE \"Id\" = {parents.Request}");

        var stored = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == parents.Request);

        Assert.Equal(ServiceRequestPaymentMode.Online, stored.PaymentMode);
        Assert.Equal(confirmedAt, stored.FinalAmountConfirmedAt);
    }

    // ─── Helpers ───

    private sealed record Parents(Guid HostOrg, Guid Property, Guid SupplierOrg, Guid Request);

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddSupplierPayments", StringComparison.Ordinal));
        Assert.True(index > 0, "AddSupplierPayments migration not found.");
        return all[index - 1];
    }

    private static async Task<int> CountColumnsAsync(AppDbContext db, string table, params string[] columns)
    {
        var names = columns.Length == 0 ? PaymentColumns : columns;
        return (int)await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM information_schema.columns WHERE table_name = {table} AND column_name = ANY({names})")
            .SingleAsync();
    }

    private static async Task<int> CountObjectsAsync(AppDbContext db, string catalog, string column, string name)
    {
        var sql = $"SELECT count(*) AS \"Value\" FROM {catalog} WHERE {column} = '{name}'";
        return (int)await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    private async Task<Parents> MigratedWithRequestAsync()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        return await SeedParentsAsync(db);
    }

    private static async Task<Parents> SeedParentsAsync(AppDbContext db)
    {
        var hostOrg = new OrgEntity
        {
            Name = "Host",
            Slug = $"sp15-host-{Guid.NewGuid():N}"[..28],
            DisplayName = "Host",
            ContactEmail = "host-sp15@example.com",
            PlanTier = PlanTier.Starter,
        };
        var supplierOrg = new OrgEntity
        {
            Name = "Supplier",
            Slug = $"sp15-sup-{Guid.NewGuid():N}"[..28],
            DisplayName = "Supplier",
            ContactEmail = "supplier-sp15@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var property = new Property
        {
            OwnerId = "auth0|sp15-migration",
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
        var request = new ServiceRequest
        {
            OrgId = hostOrg.Id,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrg.Id,
            RentalContext = ServiceRequestRentalContext.LongRent,
            Category = ServiceCategories.Cleaning,
        };
        db.AddRange(hostOrg, supplierOrg, property);
        await db.SaveChangesAsync();
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return new Parents(hostOrg.Id, property.Id, supplierOrg.Id, request.Id);
    }

    private static SupplierProfile NewProfile(Guid orgId, int? overridePercent) => new()
    {
        OrgId = orgId,
        Email = $"sp15-{Guid.NewGuid():N}@example.com",
        LegalName = "Fornitore Srl",
        Phone = "+39 06 000000",
        Status = SupplierStatus.Active,
        CategoriesJson = "[\"cleaning\"]",
        ComuniJson = "[\"H501\"]",
        CommissionPercentOverride = overridePercent,
    };

    /// <summary>A valid live payment of the request with the change applied, saved on a context of its own; returns its id.</summary>
    private async Task<Guid> SavePaymentAsync(Parents parents, Action<ServiceRequestPayment> change)
    {
        await using var db = _database!.CreateContext();
        var payment = new ServiceRequestPayment
        {
            ServiceRequestId = parents.Request,
            SupplierOrgId = parents.SupplierOrg,
            PayerKind = ServicePayerKind.Host,
            PayerOrgId = parents.HostOrg,
            AmountCents = 6_000,
            CommissionPercent = 10m,
            ApplicationFeeCents = 600,
            NetCents = 5_400,
            Status = ServicePaymentStatus.Requested,
        };
        change(payment);
        db.ServiceRequestPayments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task AssertCheckViolationAsync(Parents parents, string constraint, Action<ServiceRequestPayment> change)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SavePaymentAsync(parents, change));
        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }
}
