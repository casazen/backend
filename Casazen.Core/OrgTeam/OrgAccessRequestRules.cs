using System.Text;

namespace Casazen.Core.OrgTeam;

/// <summary>
/// The rules of the requests for access that a member sends to the administrators of its org (AM-02b): which areas and pages
/// can be asked for, what a note looks like, and how many requests a person may send a day. Pure rules, shared by the service,
/// the endpoint and the tests.
/// </summary>
public static class OrgAccessRequestRules
{
    /// <summary>The longest note a member writes to the administrators.</summary>
    public const int MaxNoteLength = 200;

    /// <summary>Requests a person may send in 24 hours, whatever the area (configurable, <see cref="DailyLimitConfigKey"/>).</summary>
    public const int DefaultDailyLimit = 3;

    public const string DailyLimitConfigKey = "OrgTeam:AccessRequestDailyLimit";

    /// <summary>The window the daily limit is counted in.</summary>
    public static readonly TimeSpan LimitWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// What a member can ask for access to: the four areas of the product (the keys of its contexts, plus <c>supplier</c>) and
    /// the pages of the account and of the rental areas that a role may not reach. A closed list on purpose: the request is
    /// written in the activity log, which holds ids and codes and no free text, and the email names the area in the
    /// language of the reader. A new page is one line here and two labels in <c>EmailTexts</c>.
    /// </summary>
    public static IReadOnlyList<string> Areas { get; } =
    [
        // The areas (as the demo's «Chiedi l'accesso» sends them).
        "account", "short-rent", "long-rent", "supplier",

        // The pages of the «Amministrazione».
        "people", "properties", "suppliers", "billing", "organization", "activity", "integrations", "security",

        // The pages of the rental areas that depend on the role.
        "payments", "reports", "prices",
    ];

    /// <summary>The area asked for, in its canonical form (trimmed, lowercase), when it is one of <see cref="Areas"/>.</summary>
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

        // Never cut inside a surrogate pair.
        var end = MaxNoteLength;
        if (char.IsHighSurrogate(line[end - 1]))
            end--;
        return line[..end].TrimEnd();
    }
}
