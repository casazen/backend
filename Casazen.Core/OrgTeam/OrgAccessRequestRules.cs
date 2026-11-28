using System.Text;

namespace Casazen.Core.OrgTeam;

public static class OrgAccessRequestRules
{
    public const int MaxNoteLength = 200;

    public const int DefaultDailyLimit = 3;

    public const string DailyLimitConfigKey = "OrgTeam:AccessRequestDailyLimit";

    public static readonly TimeSpan LimitWindow = TimeSpan.FromHours(24);

    public static IReadOnlyList<string> Areas { get; } =
    [
        "account", "short-rent", "long-rent", "supplier",

        "people", "properties", "suppliers", "billing", "organization", "activity", "integrations", "security",

        "payments", "reports", "prices",
    ];

    public static bool TryNormalizeArea(string? value, out string area)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        area = normalized;
        return Areas.Contains(normalized, StringComparer.Ordinal);
    }

    /// <summary>
    /// The note as it goes in the email: one line, with no control or invisible character (a line break, a tab, the characters
    /// that reverse the direction of a text), the white space collapsed, trimmed and at most <see cref="MaxNoteLength"/> long.
    /// Empty when there is nothing left. It is written in the email only, never stored.
    /// </summary>
    public static string NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return string.Empty;

        var text = new StringBuilder(note.Length);
        var lastWasSpace = true;
        foreach (var rune in note.EnumerateRunes())
        {
            var blank = Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is
                System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator;
            if (blank)
            {
                if (!lastWasSpace)
                    text.Append(' ');
                lastWasSpace = true;
                continue;
            }

            text.Append(rune.ToString());
            lastWasSpace = false;
        }

        var line = text.ToString().Trim();
        if (line.Length <= MaxNoteLength)
            return line;

        var end = MaxNoteLength;
        if (char.IsHighSurrogate(line[end - 1]))
            end--;
        return line[..end].TrimEnd();
    }
}
