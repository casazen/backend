using System.Text.RegularExpressions;
using Casazen.Core.Leases;

namespace Casazen.Infrastructure.Services.LeaseContracts;

/// <summary>A section of a template file: its id, optional heading and the clause text of the product owner.</summary>
public sealed record LeaseContractTemplateSection(string Id, string? Heading, string Text, IReadOnlyList<string> Placeholders);

/// <summary>Result of parsing a template file; <see cref="Errors"/> make the template incomplete.</summary>
public sealed record ParsedLeaseContractTemplate(
    string? Title,
    IReadOnlyList<LeaseContractTemplateSection> Sections,
    IReadOnlyList<string> Errors);

/// <summary>
/// Parser of the template files (format in <c>docs/runbooks/lease-contract-templates.md</c>):
/// <code>
/// # Contract title
/// ## section_id | Heading (optional)
/// Clause text with {{placeholder}}...
/// </code>
/// Whole-line <c>&lt;!-- ... --&gt;</c> comments are ignored. Nothing is invented: a section without text stays empty.
/// </summary>
public static partial class LeaseContractTemplateParser
{
    [GeneratedRegex(@"\{\{\s*([a-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    internal static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionIdPattern();

    public static ParsedLeaseContractTemplate Parse(string content)
    {
        var errors = new List<string>();
        var sections = new List<LeaseContractTemplateSection>();
        string? title = null;
        string? currentId = null;
        string? currentHeading = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (currentId is null)
                return;

            var text = string.Join('\n', buffer).Trim();
            if (sections.Any(s => s.Id == currentId))
                errors.Add($"section '{currentId}' appears more than once");
            else
                sections.Add(new LeaseContractTemplateSection(currentId, currentHeading, text, ReadPlaceholders(currentId, text, errors)));

            buffer.Clear();
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal) && trimmed.EndsWith("-->", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                (currentId, currentHeading) = ReadSectionHeader(line[3..], index + 1, errors);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (currentId is null && title is null)
                    title = line[2..].Trim();
                else
                    errors.Add($"line {index + 1}: the '# ' title is allowed once, before the first section");
                continue;
            }

            if (currentId is null)
            {
                if (trimmed.Length > 0)
                    errors.Add($"line {index + 1}: text outside of a section");
                continue;
            }

            buffer.Add(line);
        }

        Flush();

        if (title is not null && title.Contains("{{", StringComparison.Ordinal))
            errors.Add("placeholders are not allowed in the title");

        return new ParsedLeaseContractTemplate(string.IsNullOrWhiteSpace(title) ? null : title, sections, errors);
    }

    private static (string Id, string? Heading) ReadSectionHeader(string header, int lineNumber, List<string> errors)
    {
        var separator = header.IndexOf('|', StringComparison.Ordinal);
        var id = (separator < 0 ? header : header[..separator]).Trim();
        var heading = separator < 0 ? null : header[(separator + 1)..].Trim();
        if (!SectionIdPattern().IsMatch(id))
            errors.Add($"line {lineNumber}: section id '{id}' must use lower-case letters, digits and '_'");

        return (id, string.IsNullOrWhiteSpace(heading) ? null : heading);
    }

    private static IReadOnlyList<string> ReadPlaceholders(string sectionId, string text, List<string> errors)
    {
        var names = PlaceholderPattern().Matches(text)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var unknown in names.Where(n => !LeaseContractPlaceholders.IsKnown(n)))
            errors.Add($"section '{sectionId}': unknown placeholder {{{{{unknown}}}}}");

        var withoutPlaceholders = PlaceholderPattern().Replace(text, string.Empty);
        if (withoutPlaceholders.Contains("{{", StringComparison.Ordinal) || withoutPlaceholders.Contains("}}", StringComparison.Ordinal))
            errors.Add($"section '{sectionId}': malformed placeholder (use {{{{name}}}} with lower-case letters, digits and '_')");

        return names.Where(LeaseContractPlaceholders.IsKnown).ToList();
    }
}
