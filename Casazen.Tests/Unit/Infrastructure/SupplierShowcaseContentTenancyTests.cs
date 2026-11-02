using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-13a: <c>SupplierShowcaseContent</c> (the draft and the published version of a supplier's showcase) is keyed by the supplier org
/// and is not tenant-filtered (allow-list of <see cref="TenantQueryFilterArchitectureTests"/>), so its isolation rests on an explicit
/// <c>OrgId</c> predicate in every statement. These guards keep it that way: the model, the SQL of the query and the only code allowed
/// to use the table. The behavior (a supplier never reads or changes the content of another) is tested in
/// <c>SupplierShowcaseContentServiceTests</c>.
/// </summary>
public class SupplierShowcaseContentTenancyTests
{
    /// <summary>The files that may read or write the table, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierShowcaseContentService.cs"] = "The content: every statement carries the OrgId predicate.",
        ["Casazen.Infrastructure/Services/SupplierService.Maintenance.cs"] = "The repair that merges duplicate supplier profiles moves the content of the duplicate org.",
    };

    // What may follow "db.SupplierShowcaseContents" (whitespace removed): the org predicate, or an insert.
    private static readonly string[] AllowedContinuations = [".Where(c=>c.OrgId==", ".Add("];

    // Any way to reach the table from code: the DbSet through whatever the context variable is called, or Set<T>().
    private static readonly Regex AnyTableUse = new(@"\.SupplierShowcaseContents\b|Set<SupplierShowcaseContent>\(", RegexOptions.Compiled);

    // The statements of the files that are allowed to: they call the context "db".
    private static readonly Regex DbSetUse = new(@"db\.SupplierShowcaseContents(?<rest>[\s\S]{0,80})", RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwned_AndTheModelHasNoTenantFilter_ItIsAChildOfTheSupplierProfileInCascade()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(SupplierShowcaseContent)));

        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(SupplierShowcaseContent));
        Assert.NotNull(entity);

        // Not tenant-filtered: the host-org filter would hide the rows of a supplier-only account.
        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));

        // One row per supplier, a child of the supplier profile, deleted with it (the repair moves it before it deletes a profile).
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(SupplierProfile), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(SupplierShowcaseContent.OrgId), Assert.Single(foreignKey.Properties).Name);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Equal(new[] { nameof(SupplierShowcaseContent.OrgId) }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Model_VersionIsTheXminConcurrencyToken()
    {
        using var db = NewNpgsqlContext();
        var version = db.Model.FindEntityType(typeof(SupplierShowcaseContent))!.FindProperty(nameof(SupplierShowcaseContent.Version))!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal("xmin", version.GetColumnName());
        Assert.Equal("xid", version.GetColumnType());
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }

    [Fact]
    public void ContentQuery_OnTheNpgsqlProvider_CarriesTheSupplierOrgPredicateInSql()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierShowcaseContentService.ContentsOf(db, Guid.NewGuid()).AsNoTracking().ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
    }

    [Fact]
    public void TheTable_IsUsedOnlyByTheContentServiceTheRepairAndTheContext()
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
            "SupplierShowcaseContents is not tenant-filtered (keyed by the supplier org): any other reader must filter by the " +
            "supplier OrgId itself. Use SupplierShowcaseContentService, or add the file to AllowedFiles with its reason and an " +
            "explicit OrgId predicate: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/SupplierShowcaseContentService.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierService.Maintenance.cs")]
    public void EveryStatementOnTheTable_CarriesTheOrgPredicateOrIsAnInsert(string relative)
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
        var statements = DbSetUse.Matches(text);
        Assert.NotEmpty(statements);

        var offenders = statements
            .Select(m => Regex.Replace(m.Groups["rest"].Value, @"\s+", string.Empty))
            .Where(rest => !AllowedContinuations.Any(allowed => rest.StartsWith(allowed, StringComparison.Ordinal)))
            .Select(rest => rest[..Math.Min(rest.Length, 60)])
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{relative}: a statement on SupplierShowcaseContents without the explicit OrgId predicate: {string.Join(" | ", offenders)}");
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
