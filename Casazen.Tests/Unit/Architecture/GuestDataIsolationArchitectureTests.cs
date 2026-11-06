using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Architecture;

/// <summary>
/// Q1-05, AC-NF40-6, AC-NF40-7, AC-NF88-6: the data of the stay guests (the Alloggiati record, the identity document, the tourist tax exemption)
/// never feeds the CRM, its segments or the campaigns. The CRM reads the bookers' contacts and consents only; a guest registered for the police
/// is not a contact. The guard reads the source of every CRM, segment, campaign, contact and suppression file: none may name a stay guest, a
/// document field, a tax exemption or an Alloggiati report.
/// </summary>
public class GuestDataIsolationArchitectureTests
{
    /// <summary>The files of the CRM and of what it feeds, by name.</summary>
    private static readonly Regex CrmFile = new(
        "(Crm|Segment|Campaign|GuestContact|GuestMatching|Suppression|Unsubscribe)[A-Za-z]*\\.cs$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What the CRM must never read.</summary>
    private static readonly (Regex Pattern, string Hint)[] Forbidden =
    [
        (new(@"\bStayGuests?\b"), "the stay guests of the police record"),
        (new(@"\bStayGuestService\b|\bIStayGuestService\b"), "the stay guests of the police record"),
        (new(@"\bDocument(Number|TypeCode|IssuePlace\w*|IssuedOn|ExpiresOn)\b"), "an identity document field"),
        (new(@"\bDocumentImages?\b"), "a document photo"),
        (new(@"\bTaxExemptionCode\b|\bStayTouristTax(Guest)?\b"), "a tourist tax exemption"),
        (new(@"\bAlloggiati(WebReport|SendAttempt|Receipt)s?\b"), "a police communication"),
    ];

    [Fact]
    public void GuestDataIsolation_StayGuestData_NeverReachesCrm_Test()
    {
        var root = FindRepositoryRoot();
        var files = new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => !IsExcluded(file.Relative) && CrmFile.IsMatch(Path.GetFileName(file.Path)))
            .ToList();

        // The guard covers the CRM: it would pass on nothing if the files moved.
        Assert.Contains(files, f => f.Relative.EndsWith("/CrmContactIndex.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Relative.EndsWith("/CampaignService.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Relative.EndsWith("/GuestSegment.cs", StringComparison.Ordinal));

        var offending = new List<string>();
        foreach (var (path, relative) in files)
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                var code = StripComment(line);
                foreach (var (pattern, hint) in Forbidden)
                {
                    if (pattern.IsMatch(code))
                        offending.Add($"{relative}:{lineNumber} ({hint}): {line.Trim()}");
                }
            }
        }

        Assert.True(
            offending.Count == 0,
            "The CRM must never read the data of the stay guests (Q1-05): no Alloggiati record, document or tax exemption in a profile, a " +
            "segment or a campaign.\n" + string.Join("\n", offending));
    }

    private static bool IsExcluded(string relativePath) =>
        relativePath.Contains("/bin/", StringComparison.Ordinal)
        || relativePath.Contains("/obj/", StringComparison.Ordinal)
        // Migrations are history: the model of every table is in them.
        || relativePath.Contains("/Migrations/", StringComparison.Ordinal);

    /// <summary>Drops a trailing <c>//</c> comment, so a file may explain the rule it keeps.</summary>
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
