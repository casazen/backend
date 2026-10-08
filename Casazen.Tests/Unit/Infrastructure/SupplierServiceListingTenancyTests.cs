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
/// SP-02: <c>SupplierServiceListing</c> is keyed by the supplier org and is not tenant-filtered (allow-list of
/// <see cref="TenantQueryFilterArchitectureTests"/>), so its isolation rests on an explicit <c>OrgId</c> predicate in every
/// statement. These guards keep it that way: the model, and the only code allowed to use the table. The behavior (two
/// suppliers never see each other's services) is tested in <c>SupplierServiceCatalogServiceTests</c> and, on PostgreSQL,
/// <c>SupplierServiceCatalogPostgresTests</c>.
/// </summary>
public class SupplierServiceListingTenancyTests
{
    /// <summary>The files that may read or write the table, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSet and the model.",
        ["Casazen.Infrastructure/Services/SupplierServiceCatalogService.cs"] = "The catalog: every statement carries the OrgId predicate.",
        ["Casazen.Infrastructure/Services/SupplierService.Maintenance.cs"] = "The repair that merges duplicate supplier profiles moves the services of the duplicate org.",
    };

    // The statements that are allowed after "db.SupplierServiceListings" (whitespace removed): the org predicate, or an insert.
    private static readonly string[] AllowedContinuations =
    [
        ".Where(l=>l.OrgId==",
        ".AsNoTracking().Where(l=>l.OrgId==",
        ".Add(",
    ];

    // Any way to reach the table from code: the DbSet through whatever the context variable is called, or Set<T>().
    private static readonly Regex AnyTableUse = new(@"\.SupplierServiceListings\b|Set<SupplierServiceListing>\(", RegexOptions.Compiled);

    // The statements of the two files that are allowed to: they call the context "db".
    private static readonly Regex DbSetUse = new(@"db\.SupplierServiceListings(?<rest>[\s\S]{0,80})", RegexOptions.Compiled);

    [Fact]
    public void Entity_IsNotTenantOwned()
    {
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(SupplierServiceListing)));
    }

    [Fact]
    public void Model_NoTenantFilter_ChildOfTheSupplierProfileInCascade_UniqueSlugOfTheServicesNotDeleted()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(SupplierServiceListing));
        Assert.NotNull(entity);

        // Not tenant-filtered: the host-org filter would hide the rows of a supplier-only account.
        Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));

        // A child of the supplier profile, deleted with it (the repair moves the services before it deletes a profile).
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(SupplierProfile), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(SupplierServiceListing.OrgId), Assert.Single(foreignKey.Properties).Name);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);

        // The slug is unique per supplier among the services that are not deleted.
        var slugIndex = Assert.Single(entity.GetIndexes(), index => index.IsUnique);
        Assert.Equal(new[] { "OrgId", "Slug" }, slugIndex.Properties.Select(p => p.Name));
        Assert.Equal("\"DeletedAt\" IS NULL", slugIndex.GetFilter());
        Assert.Equal("UIX_SupplierServiceListings_OrgId_Slug", slugIndex.GetDatabaseName());
    }

    [Fact]
    public void Model_VersionIsTheXminConcurrencyToken()
    {
        using var db = NewNpgsqlContext();
        var version = db.Model.FindEntityType(typeof(SupplierServiceListing))!.FindProperty(nameof(SupplierServiceListing.Version))!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal("xmin", version.GetColumnName());
        Assert.Equal("xid", version.GetColumnType());
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }

    [Fact]
    public void Model_DatabaseChecksMirrorTheRules()
    {
        using var db = NewNpgsqlContext();
        // The check constraints are design-time metadata: not in the read-optimized runtime model.
        var checks = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(SupplierServiceListing))!
            .GetCheckConstraints()
            .Select(c => c.Name)
            .Order()
            .ToList();

        Assert.Equal(
            new[]
            {
                "CK_SupplierServiceListings_DurationMinutes",
                "CK_SupplierServiceListings_MinNoticeHours",
                "CK_SupplierServiceListings_PriceFromCents",
                "CK_SupplierServiceListings_WeekdaysMask",
            },
            checks);
    }

    [Fact]
    public void CatalogQuery_OnTheNpgsqlProvider_CarriesTheSupplierOrgAndTheNotDeletedPredicatesInSql()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierServiceCatalogService.ListingsOf(db, Guid.NewGuid()).AsNoTracking().ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
        Assert.Contains("\"DeletedAt\" IS NULL", sql);
    }

    [Fact]
    public void SlugQuery_OnTheNpgsqlProvider_TranslatesAndStaysInsideTheSupplierOrg()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierServiceCatalogService
            .SlugsStartingWith(SupplierServiceCatalogService.ListingsOf(db, Guid.NewGuid()), "pulizia", Guid.NewGuid())
            .ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
        Assert.Contains("\"DeletedAt\" IS NULL", sql);
        Assert.Contains("\"Slug\"", sql);
        Assert.Matches("LIKE|starts_with|left\\(", sql);
        Assert.Contains("<>", sql);
    }

    [Fact]
    public void TheTable_IsUsedOnlyByTheCatalogServiceTheRepairAndTheContext()
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
            "SupplierServiceListings is not tenant-filtered (keyed by the supplier org): any other reader must filter by the " +
            "supplier OrgId itself. Use SupplierServiceCatalogService, or add the file to AllowedFiles with its reason and " +
            "an explicit OrgId predicate (the public reads of SP-09 also need the supplier's Active status): " +
            string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/SupplierServiceCatalogService.cs")]
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
            $"{relative}: a statement on SupplierServiceListings without the explicit OrgId predicate: {string.Join(" | ", offenders)}");
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
