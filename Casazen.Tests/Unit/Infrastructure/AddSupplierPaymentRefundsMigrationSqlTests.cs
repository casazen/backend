using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15b: migration <c>AddSupplierPaymentRefunds</c>. The real PostgreSQL script from the Npgsql provider (no connection): it adds
/// one table, one nullable column and one check, so it cannot rewrite or lose a row of the payments and cannot fail on the data that
/// exists (a payment has no refunds yet, a supplier written before has no end of period). The behavior on a real database is in
/// <c>AddSupplierPaymentRefundsPostgresTests</c>.
/// </summary>
public class AddSupplierPaymentRefundsMigrationSqlTests
{
    private const string Name = "AddSupplierPaymentRefunds";

    [Fact]
    public void Migration_FollowsTheSupplierPayments()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        var index = keys.FindIndex(k => k.EndsWith(Name, StringComparison.Ordinal));
        Assert.True(index > 0);
        Assert.True(index > keys.FindIndex(k => k.EndsWith("AddSupplierPayments", StringComparison.Ordinal)));
        Assert.True(keys.FindIndex(k => k.EndsWith("AddSupplierPayments", StringComparison.Ordinal)) >= 0);
    }

    [Fact]
    public void Up_AddsTheRefundsTable_TheColumnOfThePeriod_TheChecksAndTheIndexes_AndNothingElse()
    {
        using var db = NewNpgsqlContext();
        var (previous, up, _) = Scripts(db);

        Assert.False(string.IsNullOrEmpty(previous));
        Assert.Contains("CREATE TABLE \"ServiceRequestPaymentRefunds\"", up);
        Assert.Contains("ALTER TABLE \"SupplierProfiles\" ADD \"CommissionOverrideUntil\" timestamp with time zone", up);
        Assert.Contains("CONSTRAINT \"CK_ServiceRequestPaymentRefunds_Amounts\" CHECK", up);
        Assert.Contains("CONSTRAINT \"CK_ServiceRequestPaymentRefunds_Succeeded\" CHECK", up);
        Assert.Contains("ADD CONSTRAINT \"CK_SupplierProfiles_CommissionOverrideUntil\" CHECK", up);
        // A payment with refunds is never deleted by the database.
        Assert.Contains("REFERENCES \"ServiceRequestPayments\" (\"Id\") ON DELETE RESTRICT", up);

        // The keys of exactly-once: one refund per number within a payment, one per Stripe refund, one per idempotency key.
        Assert.Contains("CREATE UNIQUE INDEX \"UIX_ServiceRequestPaymentRefunds_Payment_Sequence\" ON \"ServiceRequestPaymentRefunds\" (\"ServiceRequestPaymentId\", \"Sequence\");", up);
        Assert.Contains("CREATE UNIQUE INDEX \"UIX_ServiceRequestPaymentRefunds_StripeRefundId\" ON \"ServiceRequestPaymentRefunds\" (\"StripeRefundId\") WHERE \"StripeRefundId\" IS NOT NULL;", up);
        Assert.Contains("CREATE UNIQUE INDEX \"UIX_ServiceRequestPaymentRefunds_IdempotencyKey\" ON \"ServiceRequestPaymentRefunds\" (\"IdempotencyKey\") WHERE \"IdempotencyKey\" IS NOT NULL;", up);
        Assert.Contains("CREATE INDEX \"IX_ServiceRequestPaymentRefunds_Status\" ON \"ServiceRequestPaymentRefunds\" (\"Status\");", up);

        // Nothing that rewrites or loses a row: the payments, the requests and the other tables are not touched.
        Assert.DoesNotContain("ALTER TABLE \"ServiceRequestPayments\"", up, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE \"ServiceRequests\"", up, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", up, StringComparison.Ordinal);
        Assert.Equal(1, up.Split("CREATE TABLE ", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Down_DropsOnlyWhatItAdded_AndLeavesThePaymentsAlone()
    {
        using var db = NewNpgsqlContext();
        var (_, _, down) = Scripts(db);

        Assert.Contains("DROP TABLE \"ServiceRequestPaymentRefunds\";", down);
        Assert.Contains("ALTER TABLE \"SupplierProfiles\" DROP CONSTRAINT \"CK_SupplierProfiles_CommissionOverrideUntil\";", down);
        Assert.Contains("ALTER TABLE \"SupplierProfiles\" DROP COLUMN \"CommissionOverrideUntil\";", down);
        Assert.DoesNotContain("DROP TABLE \"ServiceRequestPayments\"", down, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceRequestPayments\" DROP", down, StringComparison.Ordinal);
    }

    /// <summary>The previous migration, the script that applies this one and the script that reverts it.</summary>
    private static (string Previous, string Up, string Down) Scripts(AppDbContext db)
    {
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith(Name, StringComparison.Ordinal));
        Assert.True(index > 0, "AddSupplierPaymentRefunds migration not found.");

        var migrator = db.GetService<IMigrator>();
        return (
            keys[index - 1],
            migrator.GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]),
            migrator.GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]));
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
}
