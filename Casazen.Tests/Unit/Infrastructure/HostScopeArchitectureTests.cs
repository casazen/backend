using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class HostScopeArchitectureTests
{
    private static readonly string[] Projects = ["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"];

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
