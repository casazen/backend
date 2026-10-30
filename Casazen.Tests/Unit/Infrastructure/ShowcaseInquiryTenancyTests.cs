using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-10b: the two tables of the request on measure (<see cref="ShowcaseInquiry"/> and <see cref="ServiceRequestMessage"/>) are keyed by the
/// supplier org and are not tenant-filtered (allow-list of <see cref="TenantQueryFilterArchitectureTests"/>: the customer is anonymous, a
/// supplier-only account has no <c>User.OrgId</c>, and a host must never read them), so their isolation rests on an explicit <c>OrgId</c>
/// predicate in every statement. These guards keep it that way: the model, the SQL of the queries, and the only code allowed to use the
/// tables. The behavior is tested in <c>ShowcaseInquiryTests</c>.
/// </summary>
public class ShowcaseInquiryTenancyTests
{
    /// <summary>The files that may read or write the tables, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSets and the model.",
        ["Casazen.Infrastructure/Services/ShowcaseInquiryService.cs"] = "The request on measure: InquiriesOf carries the OrgId predicate, the rest is an insert or a remove of a row it loaded that way.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.Thread.cs"] = "The conversation of a request on measure: every read of the thread carries the OrgId predicate of the supplier, the rest is an insert.",
        ["Casazen.Infrastructure/Services/SupplierService.Maintenance.cs"] = "The repair that merges duplicate supplier profiles drops the inquiries of the duplicate org and moves the messages of its requests; both orgs are explicit.",
        ["Casazen.Infrastructure/Services/ServiceRequestExpiryService.cs"] = "System job: deletes the inquiries whose time has passed, whoever they belong to (ExpiredInquiryIdsOf reads ids only).",
    };

    private static readonly Regex AnyTableUse = new(
        @"\.(ShowcaseInquiries|ServiceRequestMessages)\b|Set<(ShowcaseInquiry|ServiceRequestMessage)>\(",
        RegexOptions.Compiled);

    private static readonly Regex DbSetUse = new(
        @"db\.(?<set>ShowcaseInquiries|ServiceRequestMessages)(?<rest>[\s\S]{0,90})",
        RegexOptions.Compiled);

    // What may follow "db.<DbSet>" (whitespace removed): the org predicate, an insert, or the removal of a row loaded that way. The expiry
    // job removes by key from a stub (RemoveRange of ids it listed itself).
    private static readonly Regex AllowedContinuation = new(
        @"^(\.AsNoTracking\(\))?\.Where\((?<x>\w+)=>\k<x>\.OrgId==|^\.Add\(|^\.Remove\(|^\.RemoveRange\(|^\.AsNoTracking\(\)\.Where\((?<y>\w+)=>\k<y>\.ExpiresAt<=",
        RegexOptions.Compiled);

    private static readonly Type[] Entities = [typeof(ShowcaseInquiry), typeof(ServiceRequestMessage)];

    [Fact]
    public void Entities_AreNotTenantOwned_AndHaveNoTenantFilter()
    {
        using var db = NewNpgsqlContext();

        Assert.All(Entities, entityType =>
        {
            Assert.False(typeof(ITenantOwned).IsAssignableFrom(entityType), entityType.Name);
            var entity = db.Model.FindEntityType(entityType);
            Assert.NotNull(entity);
            Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));
        });
    }

    [Fact]
    public void Model_TheInquiryIsAChildOfTheSupplierProfile_TheMessagesGoWithTheirRequest_AndTheWordsAreInColumnsThatSaySoOfEncryption()
    {
        using var db = NewNpgsqlContext();

        var inquiry = db.Model.FindEntityType(typeof(ShowcaseInquiry))!;
        var toSupplier = Assert.Single(inquiry.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(SupplierProfile));
        Assert.Equal(DeleteBehavior.Cascade, toSupplier.DeleteBehavior);
        var toRequest = Assert.Single(inquiry.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(ServiceRequest));
        Assert.Equal(DeleteBehavior.SetNull, toRequest.DeleteBehavior);
        Assert.Equal("PayloadEncrypted", inquiry.FindProperty(nameof(ShowcaseInquiry.Payload))!.GetColumnName());

        var message = db.Model.FindEntityType(typeof(ServiceRequestMessage))!;
        var fromRequest = Assert.Single(message.GetForeignKeys());
        Assert.Equal(typeof(ServiceRequest), fromRequest.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fromRequest.DeleteBehavior);
        Assert.Equal("BodyEncrypted", message.FindProperty(nameof(ServiceRequestMessage.Body))!.GetColumnName());
    }

    [Fact]
    public void Model_TheIndexesAndTheChecksAreTheOnesOfTheMigration()
    {
        using var db = NewNpgsqlContext();
        var inquiry = db.Model.FindEntityType(typeof(ShowcaseInquiry))!;

        var unique = Assert.Single(inquiry.GetIndexes(), index => index.IsUnique);
        Assert.Equal(new[] { "OrgId", "ClientRequestId" }, unique.Properties.Select(p => p.Name));
        Assert.Equal("UIX_ShowcaseInquiries_OrgId_ClientRequestId", unique.GetDatabaseName());
        Assert.Contains(inquiry.GetIndexes(), index => index.GetDatabaseName() == "IX_ShowcaseInquiries_ExpiresAt");

        string[] ChecksOf(Type type) => db.GetService<IDesignTimeModel>().Model
            .FindEntityType(type)!
            .GetCheckConstraints()
            .Select(c => c.Name!)
            .Order()
            .ToArray();

        Assert.Equal(["CK_ShowcaseInquiries_Expiry"], ChecksOf(typeof(ShowcaseInquiry)));
        Assert.Equal(["CK_ServiceRequestMessages_Author"], ChecksOf(typeof(ServiceRequestMessage)));
        Assert.Contains("CK_ServiceRequests_Kind", ChecksOf(typeof(ServiceRequest)));
    }

    [Fact]
    public void TheQueriesOfOneSupplier_OnTheNpgsqlProvider_CarryTheSupplierOrgPredicateInSql()
    {
        using var db = NewNpgsqlContext();
        var org = Guid.NewGuid();

        var statements = new Dictionary<string, string>
        {
            ["inquiries of the request"] = ShowcaseInquiryService.InquiriesOf(db, org).ToQueryString(),
            ["open requests of an address"] = ShowcaseInquiryService.OpenCustomRequestsOf(db, org, "0123abcd").ToQueryString(),
        };

        Assert.All(statements, statement => Assert.Matches("\"(OrgId|SupplierOrgId)\" = @", statement.Value));
    }

    [Fact]
    public void TheSystemStatement_OnTheNpgsqlProvider_IsAcrossSuppliersOnPurpose_AndReadsNothingPersonal()
    {
        using var db = NewNpgsqlContext();
        var now = new DateTime(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

        var expired = ServiceRequestExpiryService.ExpiredInquiryIdsOf(db, now).ToQueryString();

        // Only the ids of the inquiries that lapsed, oldest first, a bounded batch: no payload, no e-mail index, no token, no tenant
        // predicate (the job works across every supplier).
        Assert.Matches("\"ExpiresAt\" <= @", expired);
        Assert.Matches("SELECT \\w+\\.\"Id\"\\s+FROM \"ShowcaseInquiries\"", expired);
        Assert.Contains("LIMIT @p", expired, StringComparison.Ordinal);
        foreach (var column in new[] { "PayloadEncrypted", "EmailHash", "TokenHash" })
            Assert.DoesNotContain(column, expired, StringComparison.Ordinal);
        Assert.DoesNotMatch("\"OrgId\" = @", expired);
    }

    [Fact]
    public void TheTables_AreUsedOnlyByTheInquiryServiceTheJobsTheRepairAndTheContext()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in ReadSources(FindRepositoryRoot()))
        {
            if (!AnyTableUse.IsMatch(text) || AllowedFiles.ContainsKey(relative))
                continue;
            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "ShowcaseInquiries and ServiceRequestMessages are not tenant-filtered (keyed by the supplier org): any other reader must " +
            "filter by the supplier OrgId itself. Use ShowcaseInquiryService, or add the file to AllowedFiles with its reason and an " +
            "explicit OrgId predicate: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/ShowcaseInquiryService.cs")]
    [InlineData("Casazen.Infrastructure/Services/ServiceRequestService.Thread.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierService.Maintenance.cs")]
    [InlineData("Casazen.Infrastructure/Services/ServiceRequestExpiryService.cs")]
    public void EveryStatementOnTheTables_CarriesTheOrgPredicateOrIsAnInsertOrARemove(string relative)
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
        var statements = DbSetUse.Matches(text);
        Assert.NotEmpty(statements);

        var offenders = statements
            .Select(m => (Set: m.Groups["set"].Value, Remainder: Regex.Replace(m.Groups["rest"].Value, @"\s+", string.Empty)))
            .Where(statement => !AllowedContinuation.IsMatch(statement.Remainder))
            .Select(statement => $"{statement.Set}{statement.Remainder[..Math.Min(statement.Remainder.Length, 50)]}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{relative}: a statement on the request-on-measure tables without the explicit OrgId predicate: {string.Join(" | ", offenders)}");
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
