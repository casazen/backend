using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="SupplierFreeCalendarEvent"/> is keyed by the supplier org and not tenant-filtered (allow-list of
/// <see cref="TenantQueryFilterArchitectureTests"/>): its isolation rests on an explicit <c>OrgId</c> predicate in every statement
/// and on the few files allowed to touch it. The behavior is tested in <c>SupplierFreeEventServiceTests</c>.
/// </summary>
public class SupplierFreeEventTenancyTests
{
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierFreeEventService.cs"] = "The supplier's list and answers: every statement carries the OrgId predicate.",
        ["Casazen.Infrastructure/Services/CalendarSyncService.cs"] = "The sync, under the supplier's lock: reads and writes the rows of one supplier.",
        ["Casazen.Infrastructure/Services/SupplierService.cs"] = "UpdateCalendarSyncAsync deletes the rows of the supplier that unlinks or replaces its feed.",
    };

    private static readonly Regex AnyTableUse = new(
        @"\.SupplierFreeCalendarEvents\b|Set<SupplierFreeCalendarEvent>\(", RegexOptions.Compiled);

    private static readonly Regex DbSetUse = new(
        @"db\.SupplierFreeCalendarEvents(?<rest>[\s\S]{0,90})", RegexOptions.Compiled);

    private static readonly Regex AllowedContinuation = new(
        @"^(\.AsNoTracking\(\))?\.(Where\((?<x>\w+)=>\k<x>\.OrgId==|CountAsync\((?<y>\w+)=>\k<y>\.OrgId==|FirstOrDefaultAsync\((?<z>\w+)=>\k<z>\.OrgId==|(Add|RemoveRange)\()",
        RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwnedAndIsAChildOfTheSupplierProfileInCascade()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(SupplierFreeCalendarEvent)));

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres").Options);
        var entity = db.Model.FindEntityType(typeof(SupplierFreeCalendarEvent))!;
        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(SupplierProfile), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(SupplierFreeCalendarEvent.OrgId), nameof(SupplierFreeCalendarEvent.DecisionKey)]));
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

        Assert.True(offenders.Count == 0, "Files that use SupplierFreeCalendarEvents without being allowed: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Code_EveryStatementOnTheTable_StartsWithTheOrgPredicateOrIsAnInsert()
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
                Assert.True(AllowedContinuation.IsMatch(rest), $"{relative}: statement without OrgId predicate: db.SupplierFreeCalendarEvents{rest[..Math.Min(rest.Length, 60)]}");
            }
        }

        Assert.True(statements >= 5, "The guard found too few statements: its pattern is stale");
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
