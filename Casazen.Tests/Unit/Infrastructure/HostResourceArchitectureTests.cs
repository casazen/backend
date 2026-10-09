using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-03b guard on the resource side of the scope per property (AM-03): a <c>HostResource</c> decides whether a collaborator
/// "Solo alcuni" may act on a row only through the id of the row's property. The actions that move money for an intervention
/// (SP-15a: confirm the final amount, pay online, mark paid) build the resource from the request; this test makes sure every
/// resource built in the code carries the property id, and that the org-level shortcut (<c>HostResource.ForOrg</c>, no property,
/// no scope) is used only by the rows that really belong to the org as a whole (the guests). A new payment endpoint that builds
/// <c>new HostResource(orgId, ownerId)</c> or uses <c>ForOrg</c> fails here instead of silently skipping the scope.
/// </summary>
public class HostResourceArchitectureTests
{
    private static readonly string[] Projects = ["Casazen.Core", "Casazen.Infrastructure", "Casazen.Web"];

    /// <summary>
    /// The files that may use the org-level resource: the rows are the guests of the org (GDPR rights, the guest card), which have no
    /// property. Guests are not narrowed by the scope yet (AM-03-FU2 in the follow-ups of the team); a property-bound row never belongs here.
    /// </summary>
    private static readonly string[] OrgLevelRows =
    [
        "Casazen.Web/Controllers/GdprController.cs",
        "Casazen.Web/Controllers/GuestsController.cs",
        "Casazen.Core/Authorization/HostResource.cs",
    ];

    [Fact]
    public void EveryHostResourceBuiltByHand_CarriesThePropertyId()
    {
        var offending = new List<string>();
        var seen = 0;
        foreach (var (relative, lineNumber, line, text) in Scan("new HostResource("))
        {
            seen++;
            var arguments = ArgumentsOf(text, line);
            if (arguments.Count < 3)
                offending.Add($"{relative}:{lineNumber}: {line.Trim()}");
        }

        Assert.True(seen > 0, "The guard found no `new HostResource(` at all: the scan is hollow.");
        Assert.True(
            offending.Count == 0,
            "A HostResource without the id of its property skips the scope of a collaborator \"Solo alcuni\" (AM-03): pass the "
            + "PropertyId, or use HostResource.ForProperty(property).\n" + string.Join("\n", offending));
    }

    [Fact]
    public void TheOrgLevelResource_IsUsedOnlyByRowsThatBelongToNoProperty()
    {
        var users = Scan("HostResource.ForOrg(")
            .Select(hit => hit.Relative)
            .Where(relative => !OrgLevelRows.Contains(relative, StringComparer.Ordinal))
            .Distinct()
            .ToList();

        Assert.True(
            users.Count == 0,
            "HostResource.ForOrg skips the scope per property: it is for rows with no property (the guests). Use ForProperty or pass "
            + "the PropertyId for the row of a property, payments included.\n" + string.Join("\n", users));
    }

    [Fact]
    public void ThePaymentActionsOfTheInterventions_BuildTheirResourceFromTheRequestAndItsProperty()
    {
        // The three actions that move money (confirm the amount, pay online, mark paid), short-rent and long-rent: each one builds
        // the resource from the request it read inside the caller's scope, with the id of the property of that request.
        foreach (var controller in new[] { "ServiceRequestsController", "LongRentServiceRequestsController" })
        {
            var hits = Scan("new HostResource(existing.OrgId")
                .Where(hit => hit.Relative.EndsWith($"{controller}.cs", StringComparison.Ordinal))
                .ToList();

            Assert.True(hits.Count >= 3, $"{controller}: {hits.Count} resources built from the request, 3 expected (confirm, payment session, mark paid).");
            Assert.All(hits, hit => Assert.Contains("existing.PropertyId", hit.Text, StringComparison.Ordinal));
        }
    }

    private static List<(string Relative, int LineNumber, string Line, string Text)> Scan(string needle)
    {
        var root = FindRepositoryRoot();
        var hits = new List<(string, int, string, string)>();
        foreach (var project in Projects)
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

                var lines = File.ReadAllLines(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = StripComment(lines[i]);
                    var at = code.IndexOf(needle, StringComparison.Ordinal);
                    if (at < 0)
                        continue;

                    // The call may span lines: read until the parenthesis that closes it.
                    var text = code[at..];
                    for (var next = i + 1; next < lines.Length && !Closed(text); next++)
                        text += " " + StripComment(lines[next]).Trim();

                    hits.Add((relative, i + 1, code, text));
                }
            }
        }

        return hits;
    }

    /// <summary>The top-level arguments of the call that starts <paramref name="text"/> (<c>new HostResource(a, b(c, d), e)</c> has three).</summary>
    private static List<string> ArgumentsOf(string text, string fallback)
    {
        var open = text.IndexOf('(', StringComparison.Ordinal);
        Assert.True(open >= 0, fallback);
        var depth = 0;
        var arguments = new List<string>();
        var start = open + 1;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        var last = text[start..i].Trim();
                        if (last.Length > 0)
                            arguments.Add(last);
                        return arguments;
                    }

                    break;
                case ',' when depth == 1:
                    arguments.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        return arguments;
    }

    private static bool Closed(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return true;
        }

        return false;
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
