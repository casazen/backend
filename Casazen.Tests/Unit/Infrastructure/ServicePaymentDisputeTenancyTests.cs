using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15c-c: <c>ServicePaymentDispute</c> belongs to a <c>ServiceRequestPayment</c> and, like it, has two parties (the supplier and the payer, who may be
/// a private customer with no org), so it is not tenant-filtered (allow-list of <see cref="TenantQueryFilterArchitectureTests"/>). Its isolation rests on
/// the payment: it is read and written only by the payments service, always under the payment lock of the request or by the id of the dispute, of its
/// payment, of its request or of its supplier org (or, for the job and the admin's queue, over the disputes by design). These guards keep it that way: the
/// model, the encryption of the words of the parties, and the only code allowed to use the table. The behavior is tested in <c>ServiceDisputeTests</c>.
/// </summary>
public class ServicePaymentDisputeTenancyTests
{
    /// <summary>The files that may read or write the table, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs"] = "The payer's complaint, the supplier's reply and the payer's withdrawal: by payment id or request id, under the payment lock; the supplier's read adds its own supplier org.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs"] = "The platform admin's queue, detail and decision (AdminOnly): the queue is a read-only projection across the suppliers, on purpose; the decision is by dispute id, under the payment lock.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesJobs.cs"] = "The system job of the disputes: it runs over the disputes in a state, across every supplier, and handles each one by id under the payment lock of its request; a job has no tenant.",
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesNotices.cs"] = "The e-mails after a commit: the dispute is read by its id.",
    };

    // The statements allowed after "db.ServicePaymentDisputes" (whitespace removed): an insert, or a predicate on the dispute id, on its payment, or on its request.
    private static readonly string[] AllowedContinuations =
    [
        ".Add(",
        ".Where(d=>d.Id==",
        ".Where(d=>d.ServiceRequestPaymentId==",
        ".Where(d=>d.ServiceRequestId==",
        ".AsNoTracking().Where(d=>d.Id==",
        ".AsNoTracking().Where(d=>d.ServiceRequestPaymentId==",
        ".AsNoTracking().Where(d=>d.ServiceRequestId==",
    ];

    // The files that work over many disputes by design, with the extra statements they may use: the job filters by state and time, the admin's queue is
    // a read-only projection. Every dispute they change is still handled by id, under the payment lock.
    private static readonly IReadOnlyDictionary<string, string[]> ExtraContinuations = new Dictionary<string, string[]>
    {
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesJobs.cs"] = [".AsNoTracking().Where(d=>d.Status=="],
        ["Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs"] = [".AsNoTracking()"],
    };

    // Any way to reach the table from code: the DbSet through whatever the context variable is called, or Set<T>().
    private static readonly Regex AnyTableUse = new(@"\.ServicePaymentDisputes\b|Set<ServicePaymentDispute>\(", RegexOptions.Compiled);

    // The statements of the allowed files: they call the context "db".
    private static readonly Regex DbSetUse = new(@"db\.ServicePaymentDisputes(?<rest>[\s\S]{0,90})", RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwned()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(ServicePaymentDispute)));
    }

    [Fact]
    public void Model_NoTenantFilter_AndEveryForeignKeyRestricts()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServicePaymentDispute));
        Assert.NotNull(entity);

        // A host-org filter would hide the dispute from the supplier who must reply and from the admin who decides.
        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));

        // Nothing under a dispute is deleted by the database: a payment, a request or a supplier with a dispute cannot be removed.
        var foreignKeys = entity.GetForeignKeys().ToList();
        Assert.Equal(3, foreignKeys.Count);
        Assert.All(foreignKeys, foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        Assert.Contains(foreignKeys, f => f.PrincipalEntityType.ClrType == typeof(ServiceRequestPayment));
        Assert.Contains(foreignKeys, f => f.PrincipalEntityType.ClrType == typeof(ServiceRequest));
        Assert.Contains(foreignKeys, f => f.PrincipalEntityType.ClrType == typeof(OrgEntity));
    }

    [Fact]
    public void Model_OneOpenDisputePerPayment_ByAUniquePartialIndex()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServicePaymentDispute))!;

        var index = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == "UIX_ServicePaymentDisputes_OpenPerPayment");
        Assert.True(index.IsUnique);
        Assert.Equal(new[] { nameof(ServicePaymentDispute.ServiceRequestPaymentId) }, index.Properties.Select(p => p.Name));

        // Open means awaiting the supplier or awaiting the decision: a decided or withdrawn dispute does not stop another one from being opened.
        Assert.Equal(
            $"\"Status\" IN ({(int)ServiceDisputeStatus.AwaitingSupplier}, {(int)ServiceDisputeStatus.AwaitingDecision})",
            index.GetFilter());
        Assert.Equal(0, (int)ServiceDisputeStatus.AwaitingSupplier);
        Assert.Equal(1, (int)ServiceDisputeStatus.AwaitingDecision);
    }

    [Fact]
    public void Model_MoneyIsInIntegerCents_AndTheWordsAreStoredEncrypted()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServicePaymentDispute))!;

        Assert.Equal(typeof(int?), entity.FindProperty(nameof(ServicePaymentDispute.DecidedRefundCents))!.ClrType);
        Assert.Equal(typeof(int?), entity.FindProperty(nameof(ServicePaymentDispute.RefundTargetCents))!.ClrType);
        Assert.Equal("ServicePaymentDisputes", entity.GetTableName());

        // The column says what it holds: the three texts live in the columns whose names end in Encrypted.
        Assert.Equal("CustomerTextEncrypted", entity.FindProperty(nameof(ServicePaymentDispute.CustomerText))!.GetColumnName());
        Assert.Equal("SupplierReplyEncrypted", entity.FindProperty(nameof(ServicePaymentDispute.SupplierReply))!.GetColumnName());
        Assert.Equal("AdminNoteEncrypted", entity.FindProperty(nameof(ServicePaymentDispute.AdminNote))!.GetColumnName());
    }

    [Fact]
    public void TheWordsOfTheParties_AreOnTheEncryptedColumnsList_UnderTheirOwnPurpose()
    {
        var columns = EncryptedColumns.All.Where(c => c.EntityType == typeof(ServicePaymentDispute)).Select(c => c.Property).Order().ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ServicePaymentDispute.AdminNote),
                nameof(ServicePaymentDispute.CustomerText),
                nameof(ServicePaymentDispute.SupplierReply),
            },
            columns);
        Assert.All(
            EncryptedColumns.All.Where(c => c.EntityType == typeof(ServicePaymentDispute)),
            column => Assert.Equal("Casazen.ServicePaymentDispute", column.Purpose));

        // With Data Protection the model gives each of them its converter: what is written is never the clear text.
        using var encrypted = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            new EphemeralDataProtectionProvider());
        Assert.True(EncryptedColumns.IsConfigured(encrypted));
    }

    [Fact]
    public void Model_DatabaseChecksMirrorTheRules()
    {
        using var db = NewNpgsqlContext();
        // The check constraints are design-time metadata: not in the read-optimized runtime model.
        var model = db.GetService<IDesignTimeModel>().Model;

        var checks = model.FindEntityType(typeof(ServicePaymentDispute))!.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);
        Assert.Equal(
            new[] { "CK_ServicePaymentDisputes_Refund", "CK_ServicePaymentDisputes_Resolved", "CK_ServicePaymentDisputes_Status" },
            checks.Keys.Order().ToArray());

        // A dispute that is over says when; an open one does not.
        Assert.Contains("\"ResolvedAt\" IS NOT NULL", checks["CK_ServicePaymentDisputes_Resolved"], StringComparison.Ordinal);
        Assert.Contains("\"ResolvedAt\" IS NULL", checks["CK_ServicePaymentDisputes_Resolved"], StringComparison.Ordinal);

        // The money of a decision exists only for the customer, and the total to reach is never below what was decided.
        Assert.Contains($"\"Status\" = {(int)ServiceDisputeStatus.ResolvedForCustomer}", checks["CK_ServicePaymentDisputes_Refund"], StringComparison.Ordinal);
        Assert.Contains("\"RefundTargetCents\" >= \"DecidedRefundCents\"", checks["CK_ServicePaymentDisputes_Refund"], StringComparison.Ordinal);
    }

    [Fact]
    public void TheValuesOfTheEnums_AreStoredAsNumbers_AndNeverMove()
    {
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4 },
            new[]
            {
                (int)ServiceDisputeStatus.AwaitingSupplier, (int)ServiceDisputeStatus.AwaitingDecision, (int)ServiceDisputeStatus.ResolvedForSupplier,
                (int)ServiceDisputeStatus.ResolvedForCustomer, (int)ServiceDisputeStatus.Withdrawn,
            });
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4 },
            new[]
            {
                (int)ServiceDisputeReason.NotDone, (int)ServiceDisputeReason.Incomplete, (int)ServiceDisputeReason.PoorQuality,
                (int)ServiceDisputeReason.NotAsDescribed, (int)ServiceDisputeReason.Other,
            });
        Assert.Equal(
            new[] { 0, 1, 2 },
            new[] { (int)ServiceDisputeOpener.Customer, (int)ServiceDisputeOpener.Host, (int)ServiceDisputeOpener.System });
        Assert.Equal(5, (int)SupplierAdminAuditAction.ServiceDisputeResolved);
    }

    [Fact]
    public void ANewDispute_WaitsForTheSupplier_AndHasNoDecision()
    {
        var dispute = new ServicePaymentDispute();

        Assert.Equal(ServiceDisputeStatus.AwaitingSupplier, dispute.Status);
        Assert.Null(dispute.SupplierReply);
        Assert.Null(dispute.ResolvedAt);
        Assert.Null(dispute.DecidedRefundCents);
        Assert.Null(dispute.RefundTargetCents);
        Assert.Null(dispute.SettledAt);
        Assert.False(dispute.NoShow);
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
            "ServicePaymentDisputes is not tenant-filtered (it belongs to a payment with two parties and an anonymous payer): any other reader must " +
            "go through SupplierPaymentService (IServiceDisputeService), or be added to AllowedFiles with its reason and an explicit predicate on the " +
            "dispute, its payment or its request: " + string.Join(", ", offenders));
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
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.Disputes.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.DisputesAdmin.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.DisputesJobs.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierPaymentService.DisputesNotices.cs")]
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
            $"{relative}: a statement on ServicePaymentDisputes without an explicit predicate: {string.Join(" | ", offenders)}");
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
