using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// FN-01 (A3-31, A5-32): every text a user can read comes from <c>SharedResources*.resx</c> (Italian default, English),
/// never from a literal in the code. Validation attributes name a resource key, controllers answer with
/// <c>this.ApiProblem(status, code, key)</c> (RFC 7807 with a stable <c>code</c>) and never with the legacy
/// <c>{ error = "..." }</c> body or a plain string.
/// </summary>
public class UserMessageLocalizationArchitectureTests
{
    private static readonly Regex ErrorMessageLiteral = new(@"ErrorMessage\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);

    // BadRequest(new { error = ... }), Conflict(new { message = ... }), StatusCode(403, new { error = ... }), also over several lines.
    private static readonly Regex LegacyErrorBody = new(
        @"\b(?:BadRequest|NotFound|Conflict|Unauthorized|UnprocessableEntity|StatusCode)\((?:\s*[\w.]+\s*,)?\s*new\s*\{[^}]*?\b(?:error|message|detail)\s*=",
        RegexOptions.Compiled);

    // BadRequest("text"), NotFound($"text"), StatusCode(500, "text").
    private static readonly Regex PlainTextBody = new(
        @"\b(?:BadRequest|NotFound|Conflict|UnprocessableEntity)\(\s*\$?""|\bStatusCode\(\s*[\w.]+\s*,\s*\$?""",
        RegexOptions.Compiled);

    // ErrorMessage = "..." anywhere in the application code must be a resource key.
    [Fact]
    public void ValidationAttributes_ErrorMessageLiteral_IsAResourceKey()
    {
        var root = FindRepositoryRoot();
        var keys = ReadResourceKeys(root);
        var offending = new List<string>();

        foreach (var (relative, text) in ReadSources(root, ["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"]))
        {
            foreach (Match match in ErrorMessageLiteral.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (!keys.Contains(value))
                    offending.Add($"{relative}:{LineOf(text, match.Index)}: ErrorMessage = \"{value}\"");
            }
        }

        Assert.True(
            offending.Count == 0,
            "ErrorMessage is not a key of Casazen.Web/Resources/SharedResources.resx (an inline text is never translated): " +
            "add the key in Italian and English and use it.\n" + string.Join("\n", offending));
    }

    [Fact]
    public void Controllers_DoNotAnswerWithLegacyErrorBodies()
    {
        var root = FindRepositoryRoot();
        var offending = new List<string>();

        foreach (var (relative, text) in ReadSources(root, ["Casazen.Web/Controllers"]))
        {
            foreach (Match match in LegacyErrorBody.Matches(text))
                offending.Add($"{relative}:{LineOf(text, match.Index)}: {FirstLine(match.Value)}");
        }

        Assert.True(
            offending.Count == 0,
            "Legacy { error/message } body: answer with this.ApiProblem(status, code, resourceKey) so the response is a " +
            "localized ProblemDetails with a stable code.\n" + string.Join("\n", offending));
    }

    [Fact]
    public void Controllers_DoNotAnswerWithPlainTextMessages()
    {
        var root = FindRepositoryRoot();
        var offending = new List<string>();

        foreach (var (relative, text) in ReadSources(root, ["Casazen.Web/Controllers"]))
        {
            // The OTA webhooks answer machines (the partner platforms), not people: their plain status texts stay.
            if (relative.EndsWith("/WebhooksController.cs", StringComparison.Ordinal))
                continue;

            foreach (Match match in PlainTextBody.Matches(text))
                offending.Add($"{relative}:{LineOf(text, match.Index)}: {FirstLine(match.Value)}");
        }

        Assert.True(
            offending.Count == 0,
            "Plain text response: answer with this.ApiProblem(status, code, resourceKey).\n" + string.Join("\n", offending));
    }

    private static HashSet<string> ReadResourceKeys(string root)
    {
        var path = Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.resx");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(e => (string)e.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string Relative, string Text)> ReadSources(string root, string[] folders)
    {
        foreach (var folder in folders)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
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

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string FirstLine(string value) => value.Split('\n')[0].Trim();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
