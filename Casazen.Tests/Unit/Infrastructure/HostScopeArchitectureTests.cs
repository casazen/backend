using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-03 guard: the reach of a caller on the properties of its org is decided in one place. A list query narrows by
/// <c>InScope(scope)</c>, which asks the database (an <c>EXISTS</c> on the grants of the person); nobody builds the filter by
/// hand from <c>HostScope.OwnerId</c> as the thirteen filters of TN-3 did, and nobody reads the reach from the token again
/// (<c>GetHostScope</c> and <c>HasOrgWideHostAccess</c> are gone: the reach comes from <c>IHostScopeResolver</c>, async, from the
/// membership in the database). A new list that forgets the rule is a review comment; one that re-introduces the old way fails here.
/// </summary>
public class HostScopeArchitectureTests
{
    private static readonly string[] Projects = ["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"];

    /// <summary>The files that define the rule and so spell it out.</summary>
    private static readonly string[] Definitions =
    [
        "Casazen.Core/Authorization/HostScope.cs",
        "Casazen.Core/Authorization/HostScopeQueryExtensions.cs",
        "Casazen.Infrastructure/Services/HostScopeResolver.cs",
    ];

    [Fact]
    public void ListQueries_NeverFilterByTheOwnerOfTheScopeByHand()
    {
        var offending = Scan(new Regex(@"\bscope\.OwnerId\b"));

        Assert.True(
            offending.Count == 0,
            "A query reads HostScope.OwnerId (AM-03): narrow it with InScope(scope) from HostScopeQueryExtensions, which also covers "
            + "the collaborators limited to some properties.\n" + string.Join("\n", offending));
    }

    [Fact]
    public void TheReachOfTheCaller_IsNeverReadFromTheTokenAgain()
    {
        var offending = Scan(new Regex(@"\b(GetHostScope|HasOrgWideHostAccess)\b"));

        Assert.True(
            offending.Count == 0,
            "The scope is resolved from the org membership in the database (IHostScopeResolver, AM-03), not from the token roles.\n"
            + string.Join("\n", offending));
    }

    [Fact]
    public void InScope_IsUsedByTheListsOfTheHost()
    {
        // The guard above would pass on an empty tree: say where the rule is applied, so a rename cannot hollow it out.
        var users = Scan(new Regex(@"\.InScope\("), includeDefinitions: false)
            .Select(hit => hit.Split(':')[0])
            .Distinct()
            .ToList();

        foreach (var expected in new[]
                 {
                     "Casazen.Infrastructure/Repositories/BookingRepositories.cs",
                     "Casazen.Infrastructure/Repositories/LeaseContractRepository.cs",
                     "Casazen.Infrastructure/Repositories/PaymentRepositories.cs",
                     "Casazen.Infrastructure/Repositories/PropertyRepositories.cs",
                     "Casazen.Infrastructure/Services/FiscalService.Reports.cs",
                     "Casazen.Infrastructure/Services/HostDashboardService.cs",
                     "Casazen.Infrastructure/Services/OnSiteBookingRequestService.cs",
                     "Casazen.Infrastructure/Services/ServiceRequestService.cs",
                     "Casazen.Infrastructure/Services/ComplianceWizardService.cs",
                     "Casazen.Infrastructure/External/AlloggiatiWebService.cs",
                 })
        {
            Assert.Contains(expected, users);
        }
    }

    private static List<string> Scan(Regex pattern, bool includeDefinitions = false)
    {
        var root = FindRepositoryRoot();
        var hits = new List<string>();
        foreach (var project in Projects)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal)
                    || (!includeDefinitions && Definitions.Contains(relative, StringComparer.Ordinal)))
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (pattern.IsMatch(StripComment(line)))
                        hits.Add($"{relative}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        return hits;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 || line.AsSpan(0, index).Contains("\"", StringComparison.Ordinal) ? line : line[..index];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
