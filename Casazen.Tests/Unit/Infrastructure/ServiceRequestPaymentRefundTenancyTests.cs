using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15b: <c>ServiceRequestPaymentRefund</c> belongs to a <c>ServiceRequestPayment</c> and, like it, has two parties (the supplier
/// and the payer, who may be a private customer with no org), so it is not tenant-filtered (allow-list of
/// <see cref="TenantQueryFilterArchitectureTests"/>). Its isolation rests on the payment: it is read and written only by the
/// payments service and the admin tools, always by the id of the refund or of the payment (or, for the jobs and the admin
/// reports, over the payments by design). These guards keep it that way: the model and the only code allowed to use the table.
/// The behavior is tested in <c>SupplierPaymentRefundTests</c>, <c>SupplierPaymentJobsTests</c> and the Postgres tests.
/// </summary>
public class ServiceRequestPaymentRefundTenancyTests
{
    /// <summary>The files that may read or write the table, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Refunds.cs"] = "The refund asked by an admin and the refund events: by refund id or by payment id, under the payment lock.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Jobs.cs"] = "The sync job looks for the refunds that wait for Stripe, across every supplier, and handles each by id under the payment lock; a job has no tenant.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Notices.cs"] = "The emails after a refund commit: the refund is read by its id.",
        ["Casazen.Infrastructure/Services/SupplierPaymentAdminService.cs"] = "The detail and the commission export of the platform admin (AdminOnly): read-only projections, on purpose.",
    };

    // The statements allowed after "db.ServiceRequestPaymentRefunds" (whitespace removed): an insert, the tracked rows, or a
    // predicate on the refund id or on the payment id.
    private static readonly string[] AllowedContinuations =
    [
        ".Add(",
        ".Local",
        ".Where(r=>r.Id==",
        ".Where(r=>r.ServiceRequestPaymentId==",
        ".AsNoTracking().Where(r=>r.Id==",
        ".AsNoTracking().Where(r=>r.ServiceRequestPaymentId==",
    ];

    // The files that work over many refunds by design, with the extra statements they may use: the jobs filter by status, the
    // export by status and month. Every refund they change is still handled by id, under the payment lock.
    private static readonly IReadOnlyDictionary<string, string[]> ExtraContinuations = new Dictionary<string, string[]>
    {
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Jobs.cs"] = [".AsNoTracking().Where(r=>r.Status==", ".AsNoTracking().Where(r=>(r.Status=="],
        ["Casazen.Infrastructure/Services/SupplierPaymentAdminService.cs"] = [".AsNoTracking().Where(r=>r.Status=="],
    };

    // Any way to reach the table from code: the DbSet through whatever the context variable is called, or Set<T>().
    private static readonly Regex AnyTableUse = new(@"\.ServiceRequestPaymentRefunds\b|Set<ServiceRequestPaymentRefund>\(", RegexOptions.Compiled);

    // The statements of the allowed files: they call the context "db".
    private static readonly Regex DbSetUse = new(@"db\.ServiceRequestPaymentRefunds(?<rest>[\s\S]{0,90})", RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwned()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(ServiceRequestPaymentRefund)));
    }

    [Fact]
    public void Model_NoTenantFilter_AndTheForeignKeyToThePaymentRestricts()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestPaymentRefund));
        Assert.NotNull(entity);

        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));

        // A payment with refunds is never removed by the database: the history of the money stays.
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(ServiceRequestPayment), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(new[] { nameof(ServiceRequestPaymentRefund.ServiceRequestPaymentId) }, foreignKey.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Model_TheKeysOfExactlyOnce_AreUniqueIndexes()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestPaymentRefund))!;

        // One refund per number within the payment: the number is the n of the key sent to Stripe.
        var sequence = Assert.Single(entity.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequestPaymentRefunds_Payment_Sequence");
        Assert.True(sequence.IsUnique);
        Assert.Equal(
            new[] { nameof(ServiceRequestPaymentRefund.ServiceRequestPaymentId), nameof(ServiceRequestPaymentRefund.Sequence) },
            sequence.Properties.Select(p => p.Name));
        Assert.Null(sequence.GetFilter());

        // One row per Stripe refund, and one per idempotency key: the webhook and the sync job can never record a refund twice.
        var stripe = Assert.Single(entity.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequestPaymentRefunds_StripeRefundId");
        Assert.True(stripe.IsUnique);
        Assert.Equal("\"StripeRefundId\" IS NOT NULL", stripe.GetFilter());

        var key = Assert.Single(entity.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequestPaymentRefunds_IdempotencyKey");
        Assert.True(key.IsUnique);
        Assert.Equal("\"IdempotencyKey\" IS NOT NULL", key.GetFilter());
    }

    [Fact]
    public void Model_MoneyIsInIntegerCents_AndTheTextsHaveALimit()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestPaymentRefund))!;

        Assert.Equal(typeof(int), entity.FindProperty(nameof(ServiceRequestPaymentRefund.AmountCents))!.ClrType);
        Assert.Equal(typeof(int?), entity.FindProperty(nameof(ServiceRequestPaymentRefund.ApplicationFeeRefundedCents))!.ClrType);
        Assert.Equal(255, entity.FindProperty(nameof(ServiceRequestPaymentRefund.StripeRefundId))!.GetMaxLength());
        Assert.Equal(255, entity.FindProperty(nameof(ServiceRequestPaymentRefund.IdempotencyKey))!.GetMaxLength());
        Assert.Equal(100, entity.FindProperty(nameof(ServiceRequestPaymentRefund.FailureCode))!.GetMaxLength());
        Assert.Equal(ServicePaymentLimits.OfflineNoteMaxLength, entity.FindProperty(nameof(ServiceRequestPaymentRefund.Reason))!.GetMaxLength());
        Assert.Equal("ServiceRequestPaymentRefunds", entity.GetTableName());
    }

    [Fact]
    public void Model_DatabaseChecksMirrorTheRules()
    {
        using var db = NewNpgsqlContext();
        // The check constraints are design-time metadata: not in the read-optimized runtime model.
        var model = db.GetService<IDesignTimeModel>().Model;

        var refund = model.FindEntityType(typeof(ServiceRequestPaymentRefund))!.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);
        Assert.Equal(
            new[] { "CK_ServiceRequestPaymentRefunds_Amounts", "CK_ServiceRequestPaymentRefunds_Succeeded" },
            refund.Keys.Order().ToArray());

        // An amount in cents within the limits of a service, and a refund that succeeded says when.
        Assert.Contains($"BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}", refund["CK_ServiceRequestPaymentRefunds_Amounts"], StringComparison.Ordinal);
        Assert.Contains($"\"Status\" <> {(int)ServicePaymentRefundStatus.Succeeded}", refund["CK_ServiceRequestPaymentRefunds_Succeeded"], StringComparison.Ordinal);
        Assert.Contains("\"CompletedAt\" IS NOT NULL", refund["CK_ServiceRequestPaymentRefunds_Succeeded"], StringComparison.Ordinal);

        // The date until which an override of the commission holds needs the override.
        var profile = model.FindEntityType(typeof(SupplierProfile))!.GetCheckConstraints().Select(c => c.Name).ToList();
        Assert.Contains("CK_SupplierProfiles_CommissionOverrideUntil", profile);
    }

    [Fact]
    public void ANewRefund_IsPendingAndAskedByAnAdmin_UntilStripeSaysOtherwise()
    {
        var refund = new ServiceRequestPaymentRefund();

        Assert.Equal(ServicePaymentRefundStatus.Pending, refund.Status);
        Assert.Equal(ServicePaymentRefundOrigin.Admin, refund.Origin);
        Assert.Null(refund.StripeRefundId);
        Assert.Null(refund.ApplicationFeeRefundedCents);
        Assert.Null(refund.CompletedAt);
        // The values of the enums are stored as numbers: they never move.
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4 },
            new[]
            {
                (int)ServicePaymentRefundStatus.Pending, (int)ServicePaymentRefundStatus.Succeeded, (int)ServicePaymentRefundStatus.Failed,
                (int)ServicePaymentRefundStatus.Canceled, (int)ServicePaymentRefundStatus.RequiresAction,
            });
        Assert.Equal(0, (int)ServicePaymentRefundOrigin.Admin);
        Assert.Equal(1, (int)ServicePaymentRefundOrigin.Stripe);
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
            "ServiceRequestPaymentRefunds is not tenant-filtered (it belongs to a payment with two parties): any other reader must " +
            "go through SupplierPaymentService, or be added to AllowedFiles with its reason and an explicit predicate on the refund " +
            "or the payment: " + string.Join(", ", offenders));
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
            $"{relative}: a statement on ServiceRequestPaymentRefunds without an explicit predicate: {string.Join(" | ", offenders)}");
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
