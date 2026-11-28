using System.Text;

namespace Casazen.Core.Suppliers;

public static class ShowcaseText
{
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

    public static bool LooksLikeMarkup(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '<' && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!' or '?'))
                return true;
        }

        return false;
    }

    private static bool IsRemoved(char c) =>
        (char.IsControl(c) && !char.IsWhiteSpace(c))
        || (int)c is 0x200B or 0x2060 or 0xFEFF
        || (int)c is >= 0x202A and <= 0x202E
        || (int)c is >= 0x2066 and <= 0x2069;
}
