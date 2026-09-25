using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// QA-CLOCK (FD-06): the calendar "today" of hosts, guests and properties is the date in Europe/Rome, from
/// <c>RomeCalendar</c> on the injected <see cref="TimeProvider"/>. The UTC date differs from it every night between
/// 22:00 and 24:00 UTC (23:00 and 24:00 in winter), so these constructions give yesterday's date in Rome and made both
/// the application and its tests wrong in that window. Technical instants (<c>DateTime.UtcNow</c>, <c>GetUtcNow()</c>)
/// stay allowed; only their truncation to a date and the server's local clock are banned.
/// </summary>
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

    /// <summary>
    /// In the tests the same constructions build a "today" that is yesterday in Rome for the code under test. Tests use
    /// a <c>FixedTimeProvider</c>/<c>FakeTimeProvider</c> or, against the real clock of an integration host,
    /// <c>TimeProvider.System.TodayInRome()</c>.
    /// </summary>
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
        // Migrations are history, and they never compute a date.
        || relativePath.Contains("/Migrations/", StringComparison.Ordinal)
        // The one place that turns an instant into the Rome calendar date.
        || relativePath.EndsWith("Casazen.Core/Utilities/RomeCalendar.cs", StringComparison.Ordinal)
        // This guard spells the banned constructions out.
        || relativePath.EndsWith("/" + nameof(CalendarTodayArchitectureTests) + ".cs", StringComparison.Ordinal);

    /// <summary>Drops a trailing <c>//</c> comment, so the rule can be explained in the code it protects.</summary>
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
