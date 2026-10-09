using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15a: <c>ServiceRequestPayment</c> has two parties, the supplier and the payer (a host org, or a private customer with no org
/// at all), so it is not tenant-filtered (allow-list of <see cref="TenantQueryFilterArchitectureTests"/>) and its isolation rests
/// on an explicit predicate in every statement: the id (the anonymous payer, who holds the link), the request, or the payer org.
/// These guards keep it that way: the model, and the only code allowed to use the table. The behavior (who sees which payment) is
/// tested in <c>SupplierPaymentSessionTests</c> and <c>ServicePaymentsIntegrationTests</c>.
/// </summary>
public class ServiceRequestPaymentTenancyTests
{
    /// <summary>The files that may read or write the table, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.cs"] = "The payments service: creates the row with the request and revokes a link.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Sessions.cs"] = "The payer's session and page: by payment id (and token), or by request and payer org.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Supplier.cs"] = "The supplier's request and offline record: by request id, after the supplier owns the request.",
        ["Casazen.Infrastructure/Services/SupplierService.Maintenance.cs"] = "The duplicate-supplier repair (SU-14) moves the payments of a merged supplier to the keeper: one update scoped by the duplicate's SupplierOrgId.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Webhook.cs"] = "The Stripe webhook (SP-15b): the payment of a PaymentIntent, found by the PaymentIntent or the payment id, then read by id under the payment lock.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Refunds.cs"] = "The admin's refund and the refund events (SP-15b): by payment id or by PaymentIntent, under the payment lock.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Jobs.cs"] = "The system jobs (SP-15b: sync, reminders, pending requests) run over the payments in a state, across every supplier, and handle each payment by id under its lock; a job has no tenant.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Notices.cs"] = "The emails after a commit (SP-15b): the payment is read by its id.",
        ["Casazen.Infrastructure/Services/SupplierPaymentAdminService.cs"] = "The platform admin's list, detail and commission export (SP-15b, AdminOnly): read-only projections across the suppliers, on purpose.",
    };

    // The statements that are allowed after "db.ServiceRequestPayments" (whitespace removed): an insert, or a predicate on the
    // payment id, on the request, on the PaymentIntent (the webhook), or (for the host) on the request and the payer org.
    private static readonly string[] AllowedContinuations =
    [
        ".Add(",
        ".Where(p=>p.Id==",
        ".Where(p=>p.ServiceRequestId==",
        ".AsNoTracking().Where(p=>p.Id==",
        ".AsNoTracking().Where(p=>p.ServiceRequestId==",
        ".AsNoTracking().Where(p=>p.StripePaymentIntentId==",
    ];

    // The files that work over many payments by design, with the extra statements they may use: the jobs filter by state and time,
    // the admin tools read across the suppliers. Every payment they change is still handled by id under the payment lock.
    private static readonly IReadOnlyDictionary<string, string[]> ExtraContinuations = new Dictionary<string, string[]>
    {
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Jobs.cs"] = [".AsNoTracking().Where(p=>"],
        ["Casazen.Infrastructure/Services/SupplierPaymentAdminService.cs"] = [".AsNoTracking()"],
    };

    // Any way to reach the table from code: the DbSet through whatever the context variable is called, or Set<T>().
    private static readonly Regex AnyTableUse = new(@"\.ServiceRequestPayments\b|Set<ServiceRequestPayment>\(", RegexOptions.Compiled);

    // The statements of the allowed files: they call the context "db".
    private static readonly Regex DbSetUse = new(@"db\.ServiceRequestPayments(?<rest>[\s\S]{0,90})", RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwned()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(ServiceRequestPayment)));
    }

    [Fact]
    public void Model_NoTenantFilter_AndTheForeignKeysRestrict()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestPayment));
        Assert.NotNull(entity);

        // Not tenant-filtered: a host filter would hide the row from the supplier, and the payer comes with a link, with no tenant.
        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));

        // Nothing under a payment is deleted by the database: a request, a supplier or a payer org with a payment cannot be removed.
        var foreignKeys = entity.GetForeignKeys().ToList();
        Assert.Equal(3, foreignKeys.Count);
        Assert.All(foreignKeys, foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        Assert.Contains(foreignKeys, f => f.PrincipalEntityType.ClrType == typeof(ServiceRequest));
        Assert.Equal(2, foreignKeys.Count(f => f.PrincipalEntityType.ClrType == typeof(OrgEntity)));
    }

    [Fact]
    public void Model_OnePaymentPerRequestThatIsNotCanceled_AndOnePaymentPerPaymentIntent()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestPayment))!;

        var live = Assert.Single(entity.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequestPayments_ServiceRequestId_Live");
        Assert.True(live.IsUnique);
        Assert.Equal(new[] { nameof(ServiceRequestPayment.ServiceRequestId) }, live.Properties.Select(p => p.Name));
        // The only status that frees the request is Canceled.
        Assert.Equal($"\"Status\" <> {(int)ServicePaymentStatus.Canceled}", live.GetFilter());
        Assert.Equal(4, (int)ServicePaymentStatus.Canceled);

        var intent = Assert.Single(entity.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequestPayments_StripePaymentIntentId");
        Assert.True(intent.IsUnique);
        Assert.Equal("\"StripePaymentIntentId\" IS NOT NULL", intent.GetFilter());
    }

    [Fact]
    public void Model_MoneyIsInIntegerCents_AndTheCommissionIsAPercentageWithTwoDecimals()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestPayment))!;

        foreach (var name in new[]
                 {
                     nameof(ServiceRequestPayment.AmountCents), nameof(ServiceRequestPayment.ApplicationFeeCents), nameof(ServiceRequestPayment.NetCents),
                     nameof(ServiceRequestPayment.RefundedCents), nameof(ServiceRequestPayment.FeeVatCents),
                 })
        {
            Assert.Equal(typeof(int), Nullable.GetUnderlyingType(entity.FindProperty(name)!.ClrType) ?? entity.FindProperty(name)!.ClrType);
        }

        Assert.Equal("numeric(5,2)", entity.FindProperty(nameof(ServiceRequestPayment.CommissionPercent))!.GetColumnType());
        Assert.Equal("jsonb", entity.FindProperty(nameof(ServiceRequestPayment.LineItemsJson))!.GetColumnType());
        Assert.Equal(3, entity.FindProperty(nameof(ServiceRequestPayment.Currency))!.GetMaxLength());
    }

    [Fact]
    public void Model_TheFeeVatOfTheCommission_IsEmptyByDefault_UntilTheConsultantDecides()
    {
        // [CONSULENTE FISCALE] (decision D4): the schema is ready, nothing decides yet.
        var payment = new ServiceRequestPayment();

        Assert.Null(payment.FeeVatMode);
        Assert.Null(payment.FeeVatCents);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Equal(ServicePayerKind.Host, payment.PayerKind);
        Assert.Equal("eur", payment.Currency);
    }

    [Fact]
    public void Model_DatabaseChecksMirrorTheRules()
    {
        using var db = NewNpgsqlContext();
        // The check constraints are design-time metadata: not in the read-optimized runtime model.
        var model = db.GetService<IDesignTimeModel>().Model;

        var payment = model.FindEntityType(typeof(ServiceRequestPayment))!.GetCheckConstraints().Select(c => c.Name).Order().ToList();
        Assert.Equal(
            new[]
            {
                "CK_ServiceRequestPayments_Amounts",
                "CK_ServiceRequestPayments_CommissionPercent",
                "CK_ServiceRequestPayments_FeeVat",
                "CK_ServiceRequestPayments_Paid",
                "CK_ServiceRequestPayments_Payer",
            },
            payment);

        var profile = model.FindEntityType(typeof(SupplierProfile))!.GetCheckConstraints().Select(c => c.Name).ToList();
        Assert.Contains("CK_SupplierProfiles_CommissionPercentOverride", profile);
    }

    [Fact]
    public void TheRequestsAndTheSupplierProfile_GetTheNewColumns_WithTheRightDefaults()
    {
        using var db = NewNpgsqlContext();
        var request = db.Model.FindEntityType(typeof(ServiceRequest))!;
        var profile = db.Model.FindEntityType(typeof(SupplierProfile))!;

        // A request that exists before the payments is paid by hand; the column is not nullable and defaults to Manual (0).
        Assert.False(request.FindProperty(nameof(ServiceRequest.PaymentMode))!.IsNullable);
        Assert.Equal(0, (int)ServiceRequestPaymentMode.Manual);
        Assert.Equal(ServiceRequestPaymentMode.Manual, new ServiceRequest().PaymentMode);
        Assert.True(request.FindProperty(nameof(ServiceRequest.FinalAmountConfirmedAt))!.IsNullable);
        // Who made the request paid is known only from SP-15a on: the requests paid before have none and the history shows the host.
        Assert.True(request.FindProperty(nameof(ServiceRequest.PaidBy))!.IsNullable);
        // No override of the commission unless an admin sets one.
        Assert.True(profile.FindProperty(nameof(SupplierProfile.CommissionPercentOverride))!.IsNullable);
        Assert.Equal("numeric(5,2)", profile.FindProperty(nameof(SupplierProfile.CommissionPercentOverride))!.GetColumnType());
    }

    [Fact]
    public void TheTable_IsUsedOnlyByThePaymentsServiceAndTheContext()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relative, text) in ReadSources(root))
        {
            if (!AnyTableUse.IsMatch(text) || AllowedFiles.ContainsKey(relative))
                continue;
            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "ServiceRequestPayments is not tenant-filtered (two parties, an anonymous payer): any other reader must filter by an " +
            "explicit predicate itself. Use SupplierPaymentService, or add the file to AllowedFiles with its reason and an " +
            "explicit predicate on the payment id, the request or the org: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAllowedFiles_AreAllInUse()
    {
        var root = FindRepositoryRoot();

        Assert.All(AllowedFiles, file => Assert.True(
            File.Exists(Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar))), $"{file.Key} no longer exists: remove it from AllowedFiles."));
        Assert.All(AllowedFiles, file => Assert.True(file.Value.Length >= 30, $"{file.Key}: say why the file may touch the table."));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Sessions.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Supplier.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Webhook.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Refunds.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Jobs.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Notices.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentAdminService.cs")]
    public void EveryStatementOnTheTable_CarriesAnExplicitPredicateOrIsAnInsert(string relative)
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
        var statements = DbSetUse.Matches(text);
        Assert.NotEmpty(statements);

        var allowedHere = AllowedContinuations.Concat(ExtraContinuations.GetValueOrDefault(relative) ?? []).ToList();
        var offenders = statements
            .Select(m => Regex.Replace(m.Groups["rest"].Value, @"\s+", string.Empty))
            .Where(rest => !allowedHere.Any(allowed => rest.StartsWith(allowed, StringComparison.Ordinal)))
            .Select(rest => rest[..Math.Min(rest.Length, 60)])
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{relative}: a statement on ServiceRequestPayments without an explicit predicate: {string.Join(" | ", offenders)}");
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);

    private static IEnumerable<(string Relative, string Text)> ReadSources(string root)
    {
        foreach (var project in new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal))
                    continue;

                yield return (relative, File.ReadAllText(path));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
