using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class CalendarTodayArchitectureTests
{
    private static readonly (Regex Pattern, string Hint)[] ForbiddenInApplication =
    [
        (new(@"\bDateTime\.Today\b"), "server-local date"),
        (new(@"\bDateTime(Offset)?\.Now\b"), "server-local clock"),
        (new(@"\.GetLocalNow\(\)"), "server-local clock"),
        (new(@"\bUtcNow(\(\))?\.Date\b"), "UTC date used as today"),
        (new(@"\bUtcNow(\(\))?\.(Year|Month|Day)\b"), "UTC calendar year, month or day"),
        (new(@"\bUtcDateTime\.Date\b"), "UTC date used as today"),
        (new(@"\b(now|utcNow|nowUtc)\.Date\b"), "UTC date used as today"),
        (new(@"DateOnly\.FromDateTime\(\s*DateTime(Offset)?\.(Utc)?Now\b"), "UTC date used as today"),
    ];

    private static readonly (Regex Pattern, string Hint)[] ForbiddenInTests =
    [
        (new(@"\bDateTime\.Today\b"), "server-local date"),
        (new(@"\bDateTime(Offset)?\.Now\b"), "server-local clock"),
        (new(@"\bUtcNow(\(\))?\.Date\b"), "UTC date used as today"),
        (new(@"\bUtcDateTime\.Date\b"), "UTC date used as today"),
        (new(@"\b(now|utcNow|nowUtc)\.Date\b"), "UTC date used as today"),
        (new(@"DateOnly\.FromDateTime\(\s*DateTime(Offset)?\.(Utc)?Now\b"), "UTC date used as today"),
    ];

    [Fact]
    public void ApplicationCode_ComputesTodayOnlyThroughRomeCalendar()
    {
        var offending = Scan(["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"], ForbiddenInApplication);

        Assert.True(
            offending.Count == 0,
            "Calendar today computed from the UTC or server clock (QA-CLOCK): use timeProvider.TodayInRome() / " +
            "TodayInRomeAsDateOnly() on the injected TimeProvider, or RomeCalendar.DateInRome for a stored instant.\n" +
            string.Join("\n", offending));
    }

    [Fact]
    public void TestCode_BuildsTodayOnlyFromATimeProvider()
    {
        var offending = Scan(["Casazen.Tests"], ForbiddenInTests);

        Assert.True(
            offending.Count == 0,
            "Test date built from the UTC or local clock (QA-CLOCK): it is yesterday in Rome between 22:00 and 24:00 UTC. " +
            "Inject a FixedTimeProvider/FakeTimeProvider, or use TimeProvider.System.TodayInRome() with the real clock.\n" +
            string.Join("\n", offending));
    }

    private static List<string> Scan(string[] projects, (Regex Pattern, string Hint)[] forbidden)
    {
        var root = FindRepositoryRoot();
        var offending = new List<string>();
        foreach (var project in projects)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (IsExcluded(relative))
                    continue;

                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    var code = StripComment(line);
                    foreach (var (pattern, hint) in forbidden)
                    {
                        if (pattern.IsMatch(code))
                            offending.Add($"{relative}:{lineNumber} ({hint}): {line.Trim()}");
                    }
                }
            }
        }

        return offending;
    }

    private static bool IsExcluded(string relativePath) =>
        relativePath.Contains("/bin/", StringComparison.Ordinal)
        || relativePath.Contains("/obj/", StringComparison.Ordinal)
        || relativePath.Contains("/Migrations/", StringComparison.Ordinal)
        || relativePath.EndsWith("Casazen.Core/Utilities/RomeCalendar.cs", StringComparison.Ordinal)
        || relativePath.EndsWith("/" + nameof(CalendarTodayArchitectureTests) + ".cs", StringComparison.Ordinal);

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
