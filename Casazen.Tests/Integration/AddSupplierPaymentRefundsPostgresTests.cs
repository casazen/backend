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
/// SP-15b migration (<c>AddSupplierPaymentRefunds</c>) on real PostgreSQL: it applies and reverts; the table of the refunds keeps
/// what the exactly-once rules rely on (one refund per number within a payment, one per Stripe refund, one per idempotency key,
/// as many rows without them as needed); the checks refuse the rows the rules never write (an amount that is not positive cents
/// within the bound of a service, a number below one, a negative part of the commission, a refund that succeeded without saying
/// when, the end of a commission period without the commission it ends); nothing under a payment can be deleted; and the enums are
/// stored as the explicit integers the code and the webhook rely on.
/// </summary>
public class AddSupplierPaymentRefundsPostgresTests : IAsyncLifetime
{
    private static readonly string[] RefundColumns =
    [
        "Id", "ServiceRequestPaymentId", "Sequence", "AmountCents", "Status", "Origin", "StripeRefundId", "IdempotencyKey", "FailureCode",
        "Reason", "RequestedByUserId", "ApplicationFeeRefundedCents", "CreatedAt", "UpdatedAt", "CompletedAt",
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
        Assert.Equal(RefundColumns.Length, await CountColumnsAsync(db, "ServiceRequestPaymentRefunds"));
        Assert.Equal(1, await CountColumnsAsync(db, "SupplierProfiles", "CommissionOverrideUntil"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        Assert.Equal(0, await CountColumnsAsync(db, "ServiceRequestPaymentRefunds"));
        Assert.Equal(0, await CountColumnsAsync(db, "SupplierProfiles", "CommissionOverrideUntil"));
        Assert.Equal(0, await CountObjectsAsync(db, "pg_constraint", "conname", "CK_SupplierProfiles_CommissionOverrideUntil"));
        // The payments of SP-15a stay.
        Assert.True(await CountColumnsAsync(db, "ServiceRequestPayments", "RefundedCents") == 1);

        await db.Database.MigrateAsync();
        Assert.Equal(RefundColumns.Length, await CountColumnsAsync(db, "ServiceRequestPaymentRefunds"));
    }

    [PostgresFact]
    public async Task APaymentThatExistedBeforeTheMigration_KeepsItsData_AndHasNoRefunds()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        var parents = await SeedParentsAsync(db);
        var payment = parents.Payment;

        await db.Database.MigrateAsync();

        var stored = await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == payment);
        Assert.Equal(ServicePaymentStatus.Paid, stored.Status);
        Assert.Equal(6_000, stored.AmountCents);
        Assert.Equal(0, await db.ServiceRequestPaymentRefunds.CountAsync());
    }

    // ─── The checks ───

    [PostgresTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ServiceRequestLimits.MaxAmountCents + 1)]
    public async Task Checks_TheAmountIsPositiveCents_WithinTheBoundOfAService(int amount)
    {
        var parents = await MigratedWithPaymentAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPaymentRefunds_Amounts", r => r.AmountCents = amount);
    }

    [PostgresFact]
    public async Task Checks_TheNumberStartsAtOne_AndAPartOfTheCommissionIsNeverNegative()
    {
        var parents = await MigratedWithPaymentAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPaymentRefunds_Amounts", r => r.Sequence = 0);
        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPaymentRefunds_Amounts", r => r.ApplicationFeeRefundedCents = -1);
        await SaveRefundAsync(parents, r => r.ApplicationFeeRefundedCents = 0);
    }

    [PostgresFact]
    public async Task Checks_ARefundThatSucceededSaysWhen()
    {
        var parents = await MigratedWithPaymentAsync();

        await AssertCheckViolationAsync(parents, "CK_ServiceRequestPaymentRefunds_Succeeded", r => r.Status = ServicePaymentRefundStatus.Succeeded);
        await SaveRefundAsync(parents, r =>
        {
            r.Status = ServicePaymentRefundStatus.Succeeded;
            r.CompletedAt = DateTime.UtcNow;
        });
        // The other states need no date.
        foreach (var status in new[] { ServicePaymentRefundStatus.Pending, ServicePaymentRefundStatus.Failed, ServicePaymentRefundStatus.Canceled, ServicePaymentRefundStatus.RequiresAction })
            await SaveRefundAsync(parents, r => r.Status = status);
    }

    [PostgresFact]
    public async Task Checks_TheEndOfACommissionPeriodNeedsTheCommissionItEnds()
    {
        var parents = await MigratedWithPaymentAsync();
        // A fixed instant: PostgreSQL keeps microseconds, .NET 100 ns, so a computed one would not compare equal after the round trip.
        var until = new DateTime(2031, 6, 30, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = _database!.CreateContext())
        {
            var profile = NewProfile(parents.SupplierOrg, overridePercent: null);
            profile.CommissionOverrideUntil = until;
            db.SupplierProfiles.Add(profile);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal("CK_SupplierProfiles_CommissionOverrideUntil", postgres.ConstraintName);
        }

        await using (var db = _database!.CreateContext())
        {
            var profile = NewProfile(parents.SupplierOrg, overridePercent: 0);
            profile.CommissionOverrideUntil = until;
            db.SupplierProfiles.Add(profile);
            await db.SaveChangesAsync();

            var stored = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == parents.SupplierOrg);
            Assert.Equal(0m, stored.CommissionPercentOverride);
            Assert.Equal(until, stored.CommissionOverrideUntil);
        }
    }

    // ─── Keys and indexes ───

    [PostgresFact]
    public async Task Index_OneRefundPerNumberWithinAPayment_TheNextPaymentStartsAgain()
    {
        var parents = await MigratedWithPaymentAsync();
        await SaveRefundAsync(parents, r => r.Sequence = 1);
        await SaveRefundAsync(parents, r => r.Sequence = 2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRefundAsync(parents, r => r.Sequence = 2));

        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_ServiceRequestPaymentRefunds_Payment_Sequence", postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task Index_AStripeRefundBelongsToOneRefund_ManyRefundsMayHaveNone()
    {
        var parents = await MigratedWithPaymentAsync();
        await SaveRefundAsync(parents, r => r.StripeRefundId = "re_one");
        await SaveRefundAsync(parents, r => r.StripeRefundId = null);
        await SaveRefundAsync(parents, r => r.StripeRefundId = null);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRefundAsync(parents, r => r.StripeRefundId = "re_one"));

        Assert.Equal("UIX_ServiceRequestPaymentRefunds_StripeRefundId", Assert.IsType<PostgresException>(ex.InnerException).ConstraintName);
    }

    [PostgresFact]
    public async Task Index_AnIdempotencyKeyBelongsToOneRefund_ARefundMadeOutsideCasaZenHasNone()
    {
        var parents = await MigratedWithPaymentAsync();
        var key = $"service-charge-refund:{parents.Payment:N}:1";
        await SaveRefundAsync(parents, r => r.IdempotencyKey = key);
        await SaveRefundAsync(parents, r => { r.IdempotencyKey = null; r.Origin = ServicePaymentRefundOrigin.Stripe; });
        await SaveRefundAsync(parents, r => { r.IdempotencyKey = null; r.Origin = ServicePaymentRefundOrigin.Stripe; });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRefundAsync(parents, r => r.IdempotencyKey = key));

        Assert.Equal("UIX_ServiceRequestPaymentRefunds_IdempotencyKey", Assert.IsType<PostgresException>(ex.InnerException).ConstraintName);
    }

    [PostgresFact]
    public async Task ForeignKeys_APaymentWithRefundsCannotBeDeleted_AndARefundCannotPointAtNothing()
    {
        var parents = await MigratedWithPaymentAsync();
        await SaveRefundAsync(parents, _ => { });
        await using var db = _database!.CreateContext();

        var delete = await Assert.ThrowsAnyAsync<Exception>(
            () => db.Database.ExecuteSqlAsync($"DELETE FROM \"ServiceRequestPayments\" WHERE \"Id\" = {parents.Payment}"));
        var postgres = delete as PostgresException ?? delete.InnerException as PostgresException;
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres?.SqlState);

        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRefundAsync(parents with { Payment = Guid.NewGuid() }, _ => { }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task Enums_AreStoredAsTheExplicitIntegersTheCodeAndTheWebhookRelyOn()
    {
        var parents = await MigratedWithPaymentAsync();
        var id = await SaveRefundAsync(parents, r =>
        {
            r.Status = ServicePaymentRefundStatus.RequiresAction;
            r.Origin = ServicePaymentRefundOrigin.Stripe;
        });
        await using var db = _database!.CreateContext();

        var status = await db.Database.SqlQuery<int>($"SELECT \"Status\" AS \"Value\" FROM \"ServiceRequestPaymentRefunds\" WHERE \"Id\" = {id}").SingleAsync();
        var origin = await db.Database.SqlQuery<int>($"SELECT \"Origin\" AS \"Value\" FROM \"ServiceRequestPaymentRefunds\" WHERE \"Id\" = {id}").SingleAsync();

        // Pending 0, Succeeded 1, Failed 2, Canceled 3, RequiresAction 4; Admin 0, Stripe 1.
        Assert.Equal(4, status);
        Assert.Equal(1, origin);
        Assert.Equal(1, (int)ServicePaymentRefundStatus.Succeeded);
    }

    // ─── Helpers ───

    private sealed record Parents(Guid HostOrg, Guid Property, Guid SupplierOrg, Guid Request, Guid Payment);

    private int _sequence;

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddSupplierPaymentRefunds", StringComparison.Ordinal));
        Assert.True(index > 0, "AddSupplierPaymentRefunds migration not found.");
        return all[index - 1];
    }

    private static async Task<int> CountColumnsAsync(AppDbContext db, string table, params string[] columns)
    {
        var names = columns.Length == 0 ? RefundColumns : columns;
        return (int)await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM information_schema.columns WHERE table_name = {table} AND column_name = ANY({names})")
            .SingleAsync();
    }

    private static async Task<int> CountObjectsAsync(AppDbContext db, string catalog, string column, string name)
    {
        var sql = $"SELECT count(*) AS \"Value\" FROM {catalog} WHERE {column} = {Quote(name)}";
        return (int)await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    private static string Quote(string value) => "$$" + value + "$$";

    private async Task<Parents> MigratedWithPaymentAsync()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        return await SeedParentsAsync(db);
    }

    /// <summary>A host, a supplier, a request and a payment that was paid through Stripe: what a refund hangs from.</summary>
    private static async Task<Parents> SeedParentsAsync(AppDbContext db)
    {
        var hostOrg = new OrgEntity
        {
            Name = "Host",
            Slug = $"sp15b-host-{Guid.NewGuid():N}"[..28],
            DisplayName = "Host",
            ContactEmail = "host-sp15b@example.com",
            PlanTier = PlanTier.Starter,
        };
        var supplierOrg = new OrgEntity
        {
            Name = "Supplier",
            Slug = $"sp15b-sup-{Guid.NewGuid():N}"[..28],
            DisplayName = "Supplier",
            ContactEmail = "supplier-sp15b@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var property = new Property
        {
            OwnerId = "auth0|sp15b-migration",
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

        var payment = new ServiceRequestPayment
        {
            ServiceRequestId = request.Id,
            SupplierOrgId = supplierOrg.Id,
            PayerKind = ServicePayerKind.Host,
            PayerOrgId = hostOrg.Id,
            AmountCents = 6_000,
            CommissionPercent = 10m,
            ApplicationFeeCents = 600,
            NetCents = 5_400,
            Status = ServicePaymentStatus.Paid,
            PaidAt = DateTime.UtcNow,
            PaidVia = ServicePaymentChannel.Stripe,
        };
        db.ServiceRequestPayments.Add(payment);
        await db.SaveChangesAsync();
        return new Parents(hostOrg.Id, property.Id, supplierOrg.Id, request.Id, payment.Id);
    }

    private static SupplierProfile NewProfile(Guid orgId, int? overridePercent) => new()
    {
        OrgId = orgId,
        Email = $"sp15b-{Guid.NewGuid():N}@example.com",
        LegalName = "Fornitore Srl",
        Phone = "+39 06 000000",
        Status = SupplierStatus.Active,
        CategoriesJson = "[\"cleaning\"]",
        ComuniJson = "[\"H501\"]",
        CommissionPercentOverride = overridePercent,
    };

    /// <summary>A valid refund of the payment with the change applied, saved on a context of its own; returns its id.</summary>
    private async Task<Guid> SaveRefundAsync(Parents parents, Action<ServiceRequestPaymentRefund> change)
    {
        await using var db = _database!.CreateContext();
        var refund = new ServiceRequestPaymentRefund
        {
            ServiceRequestPaymentId = parents.Payment,
            Sequence = Interlocked.Increment(ref _sequence),
            AmountCents = 1_000,
            Status = ServicePaymentRefundStatus.Pending,
            Origin = ServicePaymentRefundOrigin.Admin,
        };
        change(refund);
        db.ServiceRequestPaymentRefunds.Add(refund);
        await db.SaveChangesAsync();
        return refund.Id;
    }

    private async Task AssertCheckViolationAsync(Parents parents, string constraint, Action<ServiceRequestPaymentRefund> change)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRefundAsync(parents, change));
        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }
}
