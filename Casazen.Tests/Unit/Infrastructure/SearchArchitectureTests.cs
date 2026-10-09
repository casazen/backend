using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// UI-13a guards of the global search, on the sources: the things a code review would catch and a test can keep caught. The term is
/// a person's name more often than not and is never logged; the search never reads the fields of a person that the answer must not
/// carry (phone, document, fiscal code, birth), and shows an e-mail address only masked; what the caller may search is decided by
/// the permissions and the scope (<c>SearchAccessResolver</c>), never by a role check inside the search; and the search depends on
/// no PostgreSQL extension (the environments share one database, <c>docs/runbooks/global-search.md</c> section 5).
/// </summary>
public class SearchArchitectureTests
{
    private static readonly string[] SearchFolders =
    [
        "Casazen.Core/Search",
        "Casazen.Infrastructure/Search",
        "Casazen.Web/DTOs/Search",
    ];

    private static readonly string[] SearchFiles =
    [
        "Casazen.Web/Controllers/SearchController.cs",
        "Casazen.Web/Infrastructure/SearchAccessResolver.cs",
    ];

    [Fact]
    public void TheSearchFiles_AreFound()
    {
        // The guards below would pass on an empty list: say what they look at.
        var files = Sources().Select(source => source.Path).ToList();

        Assert.Contains("Casazen.Infrastructure/Search/GlobalSearchService.cs", files);
        Assert.Contains("Casazen.Infrastructure/Search/SearchKeyModel.cs", files);
        Assert.Contains("Casazen.Core/Search/SearchText.cs", files);
        Assert.Contains("Casazen.Web/Controllers/SearchController.cs", files);
        Assert.Contains("Casazen.Web/Infrastructure/SearchAccessResolver.cs", files);
    }

    [Fact]
    public void TheTerm_IsNeverLogged()
    {
        var offending = new List<string>();
        foreach (var (path, text) in Sources())
        {
            // Every logging call, up to the end of its statement: nothing that names the term, the words or the request.
            foreach (Match call in Regex.Matches(text, @"\b[lL]ogger\s*\.\s*Log\w*\s*\([^;]*;", RegexOptions.Singleline))
            {
                if (Regex.IsMatch(call.Value, @"\b(q|term|query|tokens?|request|prefix)\b", RegexOptions.IgnoreCase))
                    offending.Add($"{path}: {call.Value.Trim()}");
            }

            if (Regex.IsMatch(text, @"\b(Console|Debug|Trace)\s*\.\s*Write"))
                offending.Add($"{path}: writes to the console or the trace");
        }

        Assert.True(
            offending.Count == 0,
            "The search term is personal data (a name): never log it, nor the words or the request that hold it.\n" + string.Join("\n", offending));
    }

    [Fact]
    public void TheController_HasNoLogger()
    {
        var controller = Sources().Single(source => source.Path == "Casazen.Web/Controllers/SearchController.cs").Text;

        Assert.DoesNotContain("ILogger", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSearch_NeverReadsThePhoneTheDocumentTheFiscalCodeOrTheBirthOfAPerson()
    {
        var offending = Sources()
            .SelectMany(source => CodeLines(source.Text)
                .Where(line => Regex.IsMatch(
                    line.Text,
                    @"\b(PhoneNumber|DocumentNumber|DocumentScanUrl|DocumentType|DocumentIssuingCountry|FiscalCode|DateOfBirth|PlaceOfBirth|Citizenship|Nationality|ContactEmail)\b"))
                .Select(line => $"{source.Path}:{line.Number}: {line.Text.Trim()}"))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "The global search must not read the sensitive fields of a person (it neither searches nor shows them).\n" + string.Join("\n", offending));
    }

    [Fact]
    public void TheGuestEmail_IsOnlyShownMasked()
    {
        var service = Sources().Single(source => source.Path == "Casazen.Infrastructure/Search/GlobalSearchService.cs").Text;

        // The address is read for the guest group and goes into the answer only through the mask.
        Assert.Contains("PersonalDataMasking.MaskEmail(", service, StringComparison.Ordinal);
        var uses = CodeLines(service).Where(line => Regex.IsMatch(line.Text, @"\b[a-z]\w*\.Email\b")).ToList();
        Assert.NotEmpty(uses);
        Assert.All(uses, line => Assert.True(
            line.Text.Contains("MaskEmail(", StringComparison.Ordinal) || line.Text.Contains(".Select(", StringComparison.Ordinal),
            $"GlobalSearchService.cs:{line.Number}: the e-mail address of a guest is used other than masked: {line.Text.Trim()}"));
    }

    [Fact]
    public void TheSearch_HasNoRoleChecksOfItsOwn()
    {
        var offending = Sources()
            .SelectMany(source => CodeLines(source.Text)
                .Where(line => Regex.IsMatch(line.Text, @"\b(IsInRole|ClaimTypes\.Role|OrgRole|UserRole|HasClaim)\b"))
                .Select(line => $"{source.Path}:{line.Number}: {line.Text.Trim()}"))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "What a caller may search comes from the policies and the scope (SearchAccessResolver), not from a role check.\n" + string.Join("\n", offending));
    }

    [Fact]
    public void TheSearch_AndItsMigration_DependOnNoPostgresExtension()
    {
        var root = FindRepositoryRoot();
        var migrationFiles = Directory
            .EnumerateFiles(Path.Combine(root, "Casazen.Infrastructure", "Migrations"), "*_AddGlobalSearchKeys*.cs")
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(path)))
            .ToList();
        Assert.NotEmpty(migrationFiles);

        var offending = Sources().Concat(migrationFiles)
            .SelectMany(source => CodeLines(source.Text)
                .Where(line => Regex.IsMatch(line.Text, @"CREATE\s+EXTENSION|PostgresExtension|pg_trgm|unaccent|btree_gin", RegexOptions.IgnoreCase))
                .Select(line => $"{source.Path}:{line.Number}: {line.Text.Trim()}"))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "The search depends on no PostgreSQL extension: the environments share one database (docs/runbooks/global-search.md section 5).\n"
            + string.Join("\n", offending));
    }

    /// <summary>The lines of code of a file (comment lines and the comment at the end of a line left out), with their numbers.</summary>
    private static IEnumerable<(int Number, string Text)> CodeLines(string text)
    {
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            var index = line.IndexOf("//", StringComparison.Ordinal);
            var code = index < 0 || line.AsSpan(0, index).Contains("\"", StringComparison.Ordinal) ? line : line[..index];
            if (!string.IsNullOrWhiteSpace(code))
                yield return (number, code);
        }
    }

    private static List<(string Path, string Text)> Sources()
    {
        var root = FindRepositoryRoot();
        var paths = SearchFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Concat(SearchFiles.Select(file => Path.Combine(root, file)));

        return paths
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(path)))
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
