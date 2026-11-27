using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The small subset of Markdown the texts of the check-in registry use (E22/F01/US02/T02), turned into the HTML fragment the guest page shows after
/// <see cref="SeoHtmlSanitizer"/>: <c># </c> and <c>## </c> headings, paragraphs, <c>- </c> lists and <c>**bold**</c>. Every character of the text is HTML-encoded first, so a
/// text can never bring a tag of its own; a line that is a whole HTML comment is dropped.
/// <para>
/// <b>Conditions.</b> A block between <c>&lt;!-- if:document-archived --&gt;</c> and <c>&lt;!-- /if --&gt;</c> is for a property that keeps the photo of the document (it is
/// left out for one that does not: Ticino, or Switzerland with a canton CasaZen does not know); <c>&lt;!-- if:document-not-archived --&gt;</c> is the opposite. The marker
/// lines are never shown, and the hash of a text is the one of the whole file, whatever the condition picks.
/// </para>
/// </summary>
internal static partial class CheckInLegalMarkdown
{
    private const string IfArchived = "<!-- if:document-archived -->";
    private const string IfNotArchived = "<!-- if:document-not-archived -->";
    private const string EndIf = "<!-- /if -->";

    public static string ToHtml(string markdown, bool documentNotArchived)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var html = new StringBuilder();
        var paragraph = new List<string>();
        var items = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;

            html.Append("<p>").Append(Inline(string.Join(' ', paragraph))).Append("</p>");
            paragraph.Clear();
        }

        void FlushList()
        {
            if (items.Count == 0)
                return;

            html.Append("<ul>");
            foreach (var item in items)
                html.Append("<li>").Append(Inline(item)).Append("</li>");

            html.Append("</ul>");
            items.Clear();
        }

        foreach (var line in Lines(markdown, documentNotArchived))
        {
            if (line.Length == 0)
            {
                FlushParagraph();
                FlushList();
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                html.Append("<h4>").Append(Inline(line[3..].Trim())).Append("</h4>");
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                html.Append("<h3>").Append(Inline(line[2..].Trim())).Append("</h3>");
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                items.Add(line[2..].Trim());
            }
            else
            {
                FlushList();
                paragraph.Add(line);
            }
        }

        FlushParagraph();
        FlushList();
        return html.ToString();
    }

    /// <summary>The lines of the text that apply to the property, trimmed, without the marker lines and the comments.</summary>
    private static IEnumerable<string> Lines(string markdown, bool documentNotArchived)
    {
        var skipping = false;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line == IfArchived)
            {
                skipping = documentNotArchived;
                continue;
            }

            if (line == IfNotArchived)
            {
                skipping = !documentNotArchived;
                continue;
            }

            if (line == EndIf)
            {
                skipping = false;
                continue;
            }

            if (skipping || (line.StartsWith("<!--", StringComparison.Ordinal) && line.EndsWith("-->", StringComparison.Ordinal)))
                continue;

            yield return line;
        }
    }

    private static string Inline(string text) =>
        BoldRegex().Replace(WebUtility.HtmlEncode(text), "<strong>$1</strong>");

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();
}
