using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02b guard: the activity log is written in one way and only by the services. A line is added by
/// <c>ActivityLog.Record</c> and by nothing else (so the catalog always checks it); nobody updates a line; a controller never
/// writes one (it would be outside the unit of work of the change it reports); and the list of events in the catalog is true:
/// every event that is not reserved is recorded by some service, and no reserved one is.
/// </summary>
public class OrgActivityArchitectureTests
{
    private const string ActivityLogFile = "Casazen.Infrastructure/Services/ActivityLog.cs";

    [Fact]
    public void ALineIsAdded_OnlyByTheActivityLog()
    {
        var offending = Scan(["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"], new Regex(@"\bOrgActivityEntries\s*\.\s*(Add|AddRange|AddAsync|Attach)\b"))
            .Where(hit => !hit.StartsWith(ActivityLogFile, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "A line of the activity log is added in one place, IActivityLog.Record, which checks it against the catalog (AM-02b).\n"
            + string.Join("\n", offending));
    }

    [Fact]
    public void ALineIsNeverUpdated()
    {
        var offending = Scan(["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"], new Regex(@"\bOrgActivityEntries\s*\.\s*(Update|UpdateRange)\b|\bOrgActivityEntry\b[^;]*\bExecuteUpdate"));

        Assert.True(
            offending.Count == 0,
            "The activity log is append-only: the only deletion is the retention (AM-02b).\n" + string.Join("\n", offending));
    }

    [Fact]
    public void OnlyTheRetentionDeletes()
    {
        var offending = Scan(["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"], new Regex(@"\bOrgActivityEntries\s*\.\s*(Remove|RemoveRange|ExecuteDelete)"))
            .Where(hit => !hit.StartsWith("Casazen.Infrastructure/Services/OrgActivityRetentionService.cs", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "Only IOrgActivityRetentionService deletes lines of the activity log (AM-02b).\n" + string.Join("\n", offending));
    }

    [Fact]
    public void AControllerNeverWritesTheLog()
    {
        var offending = Scan(["Casazen.Web/Controllers"], new Regex(@"\bIActivityLog\b|\bactivityLog\s*\.\s*Record\b"));

        Assert.True(
            offending.Count == 0,
            "The activity log is written by the services, inside the unit of work of the change they record; a controller that "
            + "records after the fact would not be in the same transaction (AM-02b).\n" + string.Join("\n", offending));
    }

    [Fact]
    public void EveryEventThatIsNotReserved_IsRecordedBySomeService_AndNoReservedOneIs()
    {
        // One pass over the services and the web project: every `OrgActivityType.Name` they spell out.
        var named = Scan(["Casazen.Infrastructure", "Casazen.Web"], new Regex(@"\bOrgActivityType\.[A-Za-z]+\b"))
            .Where(hit => !hit.StartsWith(ActivityLogFile, StringComparison.Ordinal))
            .SelectMany(hit => Regex.Matches(hit, @"\bOrgActivityType\.(?<name>[A-Za-z]+)\b").Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var recorded = Enum.GetValues<OrgActivityType>().Where(type => named.Contains(type.ToString())).ToHashSet();

        var notReserved = OrgActivityCatalog.All.Where(e => !e.Reserved).Select(e => e.Type).ToHashSet();
        var reserved = OrgActivityCatalog.All.Where(e => e.Reserved).Select(e => e.Type).ToHashSet();

        Assert.Empty(notReserved.Except(recorded));
        Assert.Empty(reserved.Intersect(recorded));
    }

    private static List<string> Scan(string[] projects, Regex pattern)
    {
        var root = FindRepositoryRoot();
        var hits = new List<string>();
        foreach (var project in projects)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal))
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
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
            return string.Empty;

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
