using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-BKP: <c>ServiceRequestGroup</c> ties a host's request to its backups, so it is read and changed by more than two parties (the host,
/// the supplier of every backup) and by a system job. It is therefore not tenant-filtered (allow-list of
/// <see cref="TenantQueryFilterArchitectureTests"/>) and its isolation rests on an explicit predicate in every statement: the group id,
/// reached through a request the caller is allowed to act on, or the host org. These guards keep it that way: the model, the SQL of the
/// job, and the only code allowed to use the table. The behavior is tested in <c>ServiceRequestBackupGroupTests</c>.
/// </summary>
public class ServiceRequestGroupTenancyTests
{
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/ServiceRequestService.Groups.cs"] =
            "The group of a request: created with it (insert), read and changed by the id, under the group's lock, and read for the host by the host org.",
        ["Casazen.Infrastructure/Services/ServiceRequestGroupRunService.cs"] =
            "The system job (service-request-groups): lists the groups with something due, across every org on purpose, ids only; each group is then resolved by id under its lock.",
    };

    private static readonly string[] AllowedContinuations =
    [
        ".Add(",
        ".FirstOrDefaultAsync(g=>g.Id==",
        ".AsNoTracking().Where(g=>g.OrgId==",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ExtraContinuations = new Dictionary<string, string[]>
    {
        ["Casazen.Infrastructure/Services/ServiceRequestGroupRunService.cs"] = [".AsNoTracking().Where(g=>g.Phase!="],
    };

    private static readonly Regex AnyTableUse = new(@"\.ServiceRequestGroups\b|Set<ServiceRequestGroup>\(", RegexOptions.Compiled);

    private static readonly Regex DbSetUse = new(@"db\.ServiceRequestGroups(?<rest>[\s\S]{0,90})", RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwned_AndHasNoTenantFilter_AndItsForeignKeysRestrict()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(ServiceRequestGroup)));
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(ServiceRequestGroup));
        Assert.NotNull(entity);

        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));
        Assert.Equal("ServiceRequestGroups", entity.GetTableName());
        var hostOrg = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(OrgEntity), hostOrg.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, hostOrg.DeleteBehavior);

        var membership = Assert.Single(
            db.Model.FindEntityType(typeof(ServiceRequest))!.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(ServiceRequestGroup));
        Assert.Equal(DeleteBehavior.Restrict, membership.DeleteBehavior);
        Assert.Equal(nameof(ServiceRequest.GroupId), Assert.Single(membership.Properties).Name);
    }

    [Fact]
    public void Model_OnePrimaryPerGroup_AtMostOnePromotedBackup_AndTheDatabaseKeepsTheRolesAndTheWindowsConsistent()
    {
        using var db = NewNpgsqlContext();
        var requests = db.Model.FindEntityType(typeof(ServiceRequest))!;
        var groups = db.Model.FindEntityType(typeof(ServiceRequestGroup))!;

        var primary = Assert.Single(requests.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequests_GroupId_Primary");
        Assert.True(primary.IsUnique);
        Assert.Equal($"\"GroupRole\" = {(int)ServiceRequestGroupRole.Primary}", primary.GetFilter());
        var promoted = Assert.Single(requests.GetIndexes(), index => index.GetDatabaseName() == "UIX_ServiceRequests_GroupId_Promoted");
        Assert.True(promoted.IsUnique);
        Assert.Equal($"\"GroupRole\" = {(int)ServiceRequestGroupRole.PromotedBackup}", promoted.GetFilter());
        Assert.Contains(requests.GetIndexes(), index => index.GetDatabaseName() == "IX_ServiceRequests_GroupId" && !index.IsUnique);
        Assert.Contains(groups.GetIndexes(), index => index.GetDatabaseName() == "IX_ServiceRequestGroups_Open");

        var design = db.GetService<IDesignTimeModel>().Model;
        Assert.Contains("CK_ServiceRequests_GroupRole", design.FindEntityType(typeof(ServiceRequest))!.GetCheckConstraints().Select(c => c.Name));
        Assert.Equal(
            ["CK_ServiceRequestGroups_Phase", "CK_ServiceRequestGroups_Windows"],
            design.FindEntityType(typeof(ServiceRequestGroup))!.GetCheckConstraints().Select(c => c.Name!).Order());

        Assert.Equal(0, (int)ServiceRequestGroupRole.None);
        Assert.Equal(1, (int)ServiceRequestGroupRole.Primary);
        Assert.Equal(2, (int)ServiceRequestGroupRole.Backup);
        Assert.Equal(3, (int)ServiceRequestGroupRole.ReplacedPrimary);
        Assert.Equal(4, (int)ServiceRequestGroupRole.PromotedBackup);
        Assert.Equal(0, (int)ServiceRequestGroupPhase.PrimaryWindow);
        Assert.Equal(1, (int)ServiceRequestGroupPhase.BackupWindow);
        Assert.Equal(2, (int)ServiceRequestGroupPhase.Closed);
    }

    [Fact]
    public void Model_TheNewColumns_AreEmptyOnEveryRequestThatExists_AndTheOrgRuleIsOnByDefault()
    {
        using var db = NewNpgsqlContext();
        var request = db.Model.FindEntityType(typeof(ServiceRequest))!;
        var org = db.Model.FindEntityType(typeof(OrgEntity))!;

        Assert.True(request.FindProperty(nameof(ServiceRequest.GroupId))!.IsNullable);
        Assert.False(request.FindProperty(nameof(ServiceRequest.GroupRole))!.IsNullable);
        Assert.Equal(ServiceRequestGroupRole.None, new ServiceRequest().GroupRole);
        Assert.True(request.FindProperty(nameof(ServiceRequest.BackupAcceptedAt))!.IsNullable);
        Assert.True(new OrgEntity().SupplierBackupsEnabled);
        Assert.Equal(true, org.FindProperty(nameof(OrgEntity.SupplierBackupsEnabled))!.GetDefaultValue());
    }

    [Fact]
    public void TheJobsStatement_ListsTheOpenGroupsWhoseWindowPassedOrWhoseRequestMovedOn_AcrossEveryOrg_OldestFirstAndCapped()
    {
        using var db = NewNpgsqlContext();

        var sql = ServiceRequestGroupRunService.DueIdsOf(db, new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc)).ToQueryString();

        Assert.Contains("\"Phase\" <> 2", sql);
        Assert.Contains("\"PrimaryDueAt\" <= @", sql);
        Assert.Contains("\"BackupDueAt\" <= @", sql);
        Assert.Contains("\"GroupRole\" IN (1, 4)", sql);
        Assert.Contains("\"CancelledBy\" = 0", sql);
        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.StartsWith("ORDER BY", orderBy);
        Assert.Matches(@"LIMIT @\w+", sql);
        Assert.Matches($@"@\w+='{ServiceRequestGroupRunService.MaxPerRun}'", sql);
        Assert.DoesNotContain("\"OrgId\" = @", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTable_IsUsedOnlyByTheGroupCodeAndTheContext()
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
            "ServiceRequestGroups is not tenant-filtered (the host, the suppliers of the backups and a system job reach it): any other reader " +
            "must filter by an explicit predicate itself. Use ServiceRequestService, or add the file to AllowedFiles with its reason and an " +
            "explicit predicate on the group id or the org: " + string.Join(", ", offenders));
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
    [InlineData("Casazen.Infrastructure/Services/ServiceRequestService.Groups.cs")]
    [InlineData("Casazen.Infrastructure/Services/ServiceRequestGroupRunService.cs")]
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
            $"{relative}: a statement on ServiceRequestGroups without an explicit predicate: {string.Join(" | ", offenders)}");
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
