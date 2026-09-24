using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Decision D3 (SE-02, SE-03): no domain of CasaZen is written in the application code or in the committed configuration.
/// Links are built from <c>App:PublicSiteBaseUrl</c> (<c>PublicSiteLinks</c>), the org subdomains from
/// <c>PublicHost:BaseDomain</c>, both without default. The scan covers the three application projects (sources,
/// <c>appsettings.json</c>, resources, email templates); tests and <c>appsettings.Testing.json</c> are fixtures.
/// </summary>
public class NoHardcodedPublicDomainTests
{
    private static readonly Regex Domain = new(@"casazen\.app", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Names that contain the domain but are not addresses of a site: the namespace of the custom claims that the Auth0
    /// Action adds to the tokens (runbook auth0.md). They must match the Action, whatever domain the web app is on.
    /// </summary>
    private static readonly string[] ClaimNamespaces =
    [
        "https://casazen.app/roles",
        "https://casazen.app/email",
    ];

    /// <summary>
    /// Occurrences owned by another task of the plan, each with the task that removes them. When that task removes the
    /// occurrence the entry becomes stale and the test fails until it is deleted here. Empty since PL-11 (A1-31): the
    /// Stripe Checkout and portal return URLs are built from <c>App:PublicSiteBaseUrl</c>.
    /// </summary>
    private static readonly (string File, string Fragment, string Reason)[] PendingElsewhere = [];

    private static readonly string[] ScannedExtensions = [".cs", ".json", ".resx", ".html", ".cshtml", ".txt"];

    [Fact]
    public void ApplicationCode_HasNoHardcodedCasazenDomain()
    {
        var offending = new List<string>();
        foreach (var (file, lineNumber, line) in ScannedLines())
        {
            var rest = ClaimNamespaces.Aggregate(line, (text, claim) => text.Replace(claim, string.Empty, StringComparison.Ordinal));
            rest = PendingElsewhere
                .Where(p => p.File == file)
                .Aggregate(rest, (text, pending) => text.Replace(pending.Fragment, string.Empty, StringComparison.Ordinal));

            if (Domain.IsMatch(rest))
                offending.Add($"{file}:{lineNumber}: {line.Trim()}");
        }

        Assert.True(
            offending.Count == 0,
            "Domain written in code (decision D3): build the URL from App:PublicSiteBaseUrl (PublicSiteLinks) or a " +
            "dedicated variable without default.\n" + string.Join("\n", offending));
    }

    [Fact]
    public void PendingElsewhere_EveryEntryStillMatches()
    {
        var lines = ScannedLines().ToList();
        var stale = PendingElsewhere
            .Where(p => !lines.Any(l => l.File == p.File && l.Line.Contains(p.Fragment, StringComparison.Ordinal)))
            .Select(p => $"{p.File}: {p.Fragment} ({p.Reason})")
            .ToList();

        Assert.True(stale.Count == 0, "Remove these entries, the domain is gone: " + string.Join("; ", stale));
    }

    private static IEnumerable<(string File, int LineNumber, string Line)> ScannedLines()
    {
        var root = FindRepositoryRoot();
        foreach (var project in new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (IsExcluded(relative) || !ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    continue;

                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    yield return (relative, lineNumber, line);
                }
            }
        }
    }

    private static bool IsExcluded(string relativePath) =>
        relativePath.Contains("/bin/", StringComparison.Ordinal)
        || relativePath.Contains("/obj/", StringComparison.Ordinal)
        // Migrations are history: their snapshots describe old schemas, never URLs.
        || relativePath.Contains("/Migrations/", StringComparison.Ordinal)
        // Test and local-only configuration: fixtures, never deployed (appsettings.Development.json is not committed).
        || relativePath.EndsWith("appsettings.Testing.json", StringComparison.Ordinal)
        || relativePath.EndsWith("appsettings.Development.json", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
