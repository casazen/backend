using System.Text;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The text of a showcase (SP-13a) is <b>plain text</b>: what a supplier types ends on a public page and in a search-engine
/// description, so it is cleaned before it is kept. No control characters, no invisible or direction-changing characters, runs of
/// white space become one, and markup is refused (the page shows text as text, but the same text is read by e-mails and by
/// crawlers' snippets).
/// </summary>
public static class ShowcaseText
{
    /// <summary>
    /// One line: trimmed, any run of white space (new lines and tabs included) collapsed into one space, characters that cannot be
    /// seen or that change the direction of the text removed. <c>null</c> when nothing is left.
    /// </summary>
    public static string? Line(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (IsRemoved(c))
                continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// A text of paragraphs: every line cleaned as <see cref="Line"/>, line breaks kept as <c>\n</c>, at most one blank line in a row,
    /// none at the ends. <c>null</c> when nothing is left.
    /// </summary>
    public static string? Paragraphs(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder(value.Length);
        var blankPending = false;
        foreach (var raw in lines)
        {
            var line = Line(raw);
            if (line is null)
            {
                blankPending = builder.Length > 0;
                continue;
            }

            if (builder.Length > 0)
                builder.Append(blankPending ? "\n\n" : "\n");
            blankPending = false;
            builder.Append(line);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// True when the text looks like markup: a <c>&lt;</c> followed by a letter, <c>/</c>, <c>!</c> or <c>?</c> (a tag, a comment, a
    /// declaration). A plain "meno di 5 minuti &lt; 10" is not markup.
    /// </summary>
    public static bool LooksLikeMarkup(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '<' && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!' or '?'))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Control characters that are not white space, the invisible ones (zero-width space U+200B, word joiner U+2060, byte-order mark
    /// U+FEFF) and the ones that change the direction of the text (embeddings and overrides U+202A to U+202E, isolates U+2066 to
    /// U+2069). The zero-width joiner stays: emoji need it.
    /// </summary>
    private static bool IsRemoved(char c) =>
        (char.IsControl(c) && !char.IsWhiteSpace(c))
        || (int)c is 0x200B or 0x2060 or 0xFEFF
        || (int)c is >= 0x202A and <= 0x202E
        || (int)c is >= 0x2066 and <= 0x2069;
}
