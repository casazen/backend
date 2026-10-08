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
/// SP-03: the four tables of the supplier's agenda (<see cref="SupplierWorkingHours"/>, <see cref="SupplierTimeOff"/>,
/// <see cref="SupplierBusyWindow"/>, <see cref="SupplierSettings"/>) are keyed by the supplier org and are not tenant-filtered
/// (allow-list of <see cref="TenantQueryFilterArchitectureTests"/>), so their isolation rests on an explicit <c>OrgId</c>
/// predicate in every statement. These guards keep it that way: the model, the SQL of the queries, and the only code allowed to
/// use the tables. The behavior (two suppliers never see each other's agenda) is tested in <c>SupplierAgendaServiceTests</c> and,
/// on PostgreSQL, <c>SupplierAgendaPostgresTests</c>.
/// </summary>
public class SupplierAgendaTenancyTests
{
    /// <summary>The files that may read or write the tables, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Data/AppDbContext.cs"] = "Declares the DbSets and the model.",
        ["Casazen.Infrastructure/Services/SupplierAgendaService.cs"] = "The agenda: every statement carries the OrgId predicate.",
        ["Casazen.Infrastructure/Services/SupplierService.Maintenance.cs"] = "The repair that merges duplicate supplier profiles moves the agenda of the duplicate org.",
    };

    private static readonly string[] DbSetNames =
        ["SupplierWorkingHours", "SupplierTimeOff", "SupplierBusyWindows", "SupplierSettings"];

    // Any way to reach a table from code: the DbSet through whatever the context variable is called, or Set<T>().
    private static readonly Regex AnyTableUse = new(
        @"\.(SupplierWorkingHours|SupplierTimeOff|SupplierBusyWindows|SupplierSettings)\b|Set<(SupplierWorkingHours|SupplierTimeOff|SupplierBusyWindow|SupplierSettings)>\(",
        RegexOptions.Compiled);

    // The statements of the two files that are allowed to: they call the context "db".
    private static readonly Regex DbSetUse = new(
        @"db\.(?<set>SupplierWorkingHours|SupplierTimeOff|SupplierBusyWindows|SupplierSettings)(?<rest>[\s\S]{0,90})",
        RegexOptions.Compiled);

    // What may follow "db.<DbSet>" (whitespace removed): the org predicate, or an insert.
    private static readonly Regex AllowedContinuation = new(
        @"^(\.AsNoTracking\(\))?\.Where\((?<x>\w+)=>\k<x>\.OrgId==|^\.Add\(",
        RegexOptions.Compiled);

    private static readonly Type[] Entities =
        [typeof(SupplierWorkingHours), typeof(SupplierTimeOff), typeof(SupplierBusyWindow), typeof(SupplierSettings)];

    [Fact]
    public void Entities_AreNotTenantOwned()
    {
        Assert.All(Entities, entity => Assert.False(typeof(ITenantOwned).IsAssignableFrom(entity), entity.Name));
    }

    [Fact]
    public void Model_NoTenantFilter_ChildrenOfTheSupplierProfileInCascade()
    {
        using var db = NewNpgsqlContext();

        Assert.All(Entities, entityType =>
        {
            var entity = db.Model.FindEntityType(entityType);
            Assert.NotNull(entity);

            // Not tenant-filtered: the host-org filter would hide the rows of a supplier-only account.
            Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));

            // A child of the supplier profile, deleted with it (the repair moves the rows before it deletes a profile).
            var foreignKey = Assert.Single(entity.GetForeignKeys());
            Assert.Equal(typeof(SupplierProfile), foreignKey.PrincipalEntityType.ClrType);
            Assert.Equal(nameof(SupplierProfile.OrgId), foreignKey.PrincipalKey.Properties.Single().Name);
            Assert.Equal(nameof(SupplierWorkingHours.OrgId), Assert.Single(foreignKey.Properties).Name);
            Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        });
    }

    [Fact]
    public void Model_TheSettingsRow_IsKeyedByTheSupplierOrg_OneRowPerSupplier()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(SupplierSettings))!;

        var key = Assert.IsAssignableFrom<IKey>(entity.FindPrimaryKey());
        Assert.Equal(nameof(SupplierSettings.OrgId), Assert.Single(key.Properties).Name);
        Assert.True(Assert.Single(entity.GetForeignKeys()).IsUnique);
        Assert.Equal("SupplierSettings", entity.GetTableName());
    }

    [Fact]
    public void Model_TheTables_HaveTheNamesAndIndexesOfTheMigration()
    {
        using var db = NewNpgsqlContext();

        Assert.Equal("SupplierWorkingHours", db.Model.FindEntityType(typeof(SupplierWorkingHours))!.GetTableName());
        Assert.Equal("SupplierTimeOff", db.Model.FindEntityType(typeof(SupplierTimeOff))!.GetTableName());
        Assert.Equal("SupplierBusyWindows", db.Model.FindEntityType(typeof(SupplierBusyWindow))!.GetTableName());

        var hours = db.Model.FindEntityType(typeof(SupplierWorkingHours))!;
        var unique = Assert.Single(hours.GetIndexes(), index => index.IsUnique);
        Assert.Equal(new[] { "OrgId", "Weekday", "StartMinute" }, unique.Properties.Select(p => p.Name));
        Assert.Equal("UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute", unique.GetDatabaseName());

        var windows = db.Model.FindEntityType(typeof(SupplierBusyWindow))!;
        Assert.Contains(windows.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { "OrgId", "StartUtc" }));

        var timeOff = db.Model.FindEntityType(typeof(SupplierTimeOff))!;
        Assert.Contains(timeOff.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { "OrgId", "FromDate" }));
    }

    [Fact]
    public void Model_DatabaseChecksMirrorTheRules()
    {
        using var db = NewNpgsqlContext();

        // The check constraints are design-time metadata: not in the read-optimized runtime model.
        string[] ChecksOf(Type type) => db.GetService<IDesignTimeModel>().Model
            .FindEntityType(type)!
            .GetCheckConstraints()
            .Select(c => c.Name!)
            .Order()
            .ToArray();

        Assert.Equal(["CK_SupplierWorkingHours_Minutes", "CK_SupplierWorkingHours_Weekday"], ChecksOf(typeof(SupplierWorkingHours)));
        Assert.Equal(["CK_SupplierTimeOff_Dates"], ChecksOf(typeof(SupplierTimeOff)));
        Assert.Equal(["CK_SupplierBusyWindows_Interval"], ChecksOf(typeof(SupplierBusyWindow)));
        Assert.Equal(
            [
                "CK_SupplierSettings_BufferMinutes",
                "CK_SupplierSettings_HorizonDays",
                "CK_SupplierSettings_MaxJobsPerDay",
                "CK_SupplierSettings_MinNoticeHours",
                "CK_SupplierSettings_ParallelJobs",
                "CK_SupplierSettings_RespondWithinMinutes",
                "CK_SupplierSettings_SlotStepMinutes",
            ],
            ChecksOf(typeof(SupplierSettings)));
    }

    [Fact]
    public void AgendaQueries_OnTheNpgsqlProvider_CarryTheSupplierOrgPredicateInSql()
    {
        using var db = NewNpgsqlContext();
        var org = Guid.NewGuid();

        var statements = new Dictionary<string, string>
        {
            ["hours"] = SupplierAgendaService.HoursOf(db, org).AsNoTracking().ToQueryString(),
            ["time off"] = SupplierAgendaService.TimeOffOf(db, org).AsNoTracking().ToQueryString(),
            ["windows"] = SupplierAgendaService.WindowsOf(db, org).AsNoTracking().ToQueryString(),
            ["manual windows"] = SupplierAgendaService.ManualWindowsOf(db, org).AsNoTracking().ToQueryString(),
            ["settings"] = SupplierAgendaService.SettingsOf(db, org).AsNoTracking().ToQueryString(),
        };

        Assert.All(statements, statement => Assert.Matches("\"OrgId\" = @", statement.Value));
    }

    [Fact]
    public void ManualWindowsQuery_LeavesOutTheEngagementsOfTheCalendarFeed()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierAgendaService.ManualWindowsOf(db, Guid.NewGuid()).AsNoTracking().ToQueryString();

        // Source = Manual (0) and Kind <> External (2): the supplier can delete only what it set by hand.
        Assert.Matches("\"Source\" = 0", sql);
        Assert.Matches("\"Kind\" <> 2", sql);
    }

    [Fact]
    public void TheTables_AreUsedOnlyByTheAgendaServiceTheRepairAndTheContext()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relative, text) in ReadSources(root))
        {
            if (AllowedFiles.ContainsKey(relative) || !AnyTableUse.IsMatch(CodeWithoutComments(text)))
                continue;
            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "The tables of the supplier's agenda are not tenant-filtered (keyed by the supplier org): any other reader must " +
            "filter by the supplier OrgId itself. Use SupplierAgendaService, or add the file to AllowedFiles with its reason and " +
            "an explicit OrgId predicate (the public slots of SP-09 read through the planner input and also need the supplier's " +
            "Active status): " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/SupplierAgendaService.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierService.Maintenance.cs")]
    public void EveryStatementOnTheTables_CarriesTheOrgPredicateOrIsAnInsert(string relative)
    {
        var text = CodeWithoutComments(File.ReadAllText(Path.Combine(FindRepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar))));
        var statements = DbSetUse.Matches(text);
        Assert.NotEmpty(statements);

        var offenders = statements
            .Select(m => (Set: m.Groups["set"].Value, Remainder: Regex.Replace(m.Groups["rest"].Value, @"\s+", string.Empty)))
            .Where(statement => !AllowedContinuation.IsMatch(statement.Remainder))
            .Select(statement => $"{statement.Set}{statement.Remainder[..Math.Min(statement.Remainder.Length, 50)]}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{relative}: a statement on a table of the agenda without the explicit OrgId predicate: {string.Join(" | ", offenders)}");
    }

    [Fact]
    public void TheAgendaService_UsesEveryTableOfTheAgenda()
    {
        // A guard on the guard: if the regexes stopped matching, the checks above would pass for nothing.
        var text = CodeWithoutComments(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "Services", "SupplierAgendaService.cs")));
        var used = DbSetUse.Matches(text).Select(m => m.Groups["set"].Value).Distinct().Order().ToList();

        Assert.Equal(DbSetNames.Order(), used);
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);

    /// <summary>The source without the lines that are only a comment (documentation names the tables without using them).</summary>
    private static string CodeWithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
