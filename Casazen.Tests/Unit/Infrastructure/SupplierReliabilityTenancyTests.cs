using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="SupplierReliabilityEvent"/> is keyed by the supplier org and not tenant-filtered (allow-list of
/// <see cref="TenantQueryFilterArchitectureTests"/>): the reliability of a supplier is read by the ranking for every host, and written
/// by the flows of the supplier, so its isolation rests on an explicit supplier predicate in every statement and on the one file
/// allowed to touch the table. The behavior is tested in <c>SupplierReliabilityServiceTests</c>.
/// </summary>
public class SupplierReliabilityTenancyTests
{
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierReliabilityService.cs"] = "The only writer and reader: every statement carries the supplier predicate.",
    };

    private static readonly Regex AnyTableUse = new(
        @"\.SupplierReliabilityEvents\b|Set<SupplierReliabilityEvent>\(", RegexOptions.Compiled);

    private static readonly Regex DbSetUse = new(
        @"db\.SupplierReliabilityEvents(?<rest>[\s\S]{0,90})", RegexOptions.Compiled);

    private static readonly Regex AllowedContinuation = new(
        @"^(\.AsNoTracking\(\))?\.(Where\((?<x>\w+)=>(\k<x>\.SupplierOrgId==|\w+\.Contains\(\k<x>\.SupplierOrgId\))|AnyAsync\((?<y>\w+)=>\k<y>\.SupplierOrgId==|Add\()",
        RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwnedAndIsAChildOfTheSupplierProfileInCascade()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(SupplierReliabilityEvent)));

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres").Options);
        var entity = db.Model.FindEntityType(typeof(SupplierReliabilityEvent))!;
        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(SupplierProfile), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        // One event of a kind for each supplier and request: the writers are jobs and webhooks that may run twice.
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(SupplierReliabilityEvent.SupplierOrgId), nameof(SupplierReliabilityEvent.RequestId), nameof(SupplierReliabilityEvent.Kind)]));
    }

    [Fact]
    public void Code_OnlyTheAllowedFilesTouchTheTable()
    {
        var root = FindRepoRoot();
        var offenders = SourceFiles(root)
            .Where(f => AnyTableUse.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !AllowedFiles.ContainsKey(f))
            .ToList();

        Assert.True(offenders.Count == 0, "Files that use SupplierReliabilityEvents without being allowed: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Code_EveryStatementOnTheTable_StartsWithTheSupplierPredicateOrIsAnInsert()
    {
        var root = FindRepoRoot();
        var statements = 0;
        foreach (var relative in AllowedFiles.Keys.Where(k => !k.EndsWith("AppDbContext.cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            foreach (Match match in DbSetUse.Matches(text))
            {
                statements++;
                var rest = Regex.Replace(match.Groups["rest"].Value, @"\s+", string.Empty);
                Assert.True(AllowedContinuation.IsMatch(rest), $"{relative}: statement without a supplier predicate: db.SupplierReliabilityEvents{rest[..Math.Min(rest.Length, 60)]}");
            }
        }

        Assert.True(statements >= 3, "The guard found too few statements: its pattern is stale");
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Casazen.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Casazen.sln not found");
    }
}
