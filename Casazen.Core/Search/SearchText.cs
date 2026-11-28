using System.Text;
using Casazen.Core.Services;

namespace Casazen.Core.Search;

/// <summary>
/// What the global search (UI-13a) does to a text before it compares it: the same folding for the words typed by the user and
/// for the words stored in the database, and the rules of the query (length, number of words). Pure and culture independent.
/// </summary>
/// <remarks>
/// <para><b>Folding.</b> <see cref="Fold"/> lower-cases, drops the accents of <see cref="SearchFolding"/> and turns every run of
/// characters that are not an ASCII letter or digit into one space: the result is words made of <c>a-z</c> and <c>0-9</c>
/// separated by single spaces. The database does the same to the text of its rows with a stored generated column
/// (<c>SearchKey</c>); the two are written from one table so that a word typed matches the word stored.</para>
/// <para><b>Matching.</b> A term matches a row when every word of the term is the <i>beginning of a word</i> of the row's key:
/// "ros mar" finds "Maria Rossi" and "Mario De Rossi", not "Carlo Sorrosi". PostgreSQL does it with a full-text index on the key
/// (<c>to_tsvector('simple', "SearchKey") @@ to_tsquery('simple', 'ros:* &amp; mar:*')</c>, built from <see cref="SearchQuery.ToTsQuery"/>);
/// <see cref="KeyMatches"/> is the same rule in C#, used where there is no PostgreSQL (the in-memory provider of the unit tests).</para>
/// </remarks>
public static class SearchText
{
    public const int MinQueryLength = 2;

    public const int MaxQueryLength = 64;

    public const int MaxTokens = 5;

    public const int MinBookingCodeLength = 4;

    private const int MaxBookingCodeParts = 2;

    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var folded = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var character in value)
        {
            var isAsciiWord = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            string? letters = null;
            if (!isAsciiWord && !SearchFolding.TryFold(character, out letters))
            {
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && folded.Length > 0)
                folded.Append(' ');
            pendingSeparator = false;

            if (isAsciiWord)
                folded.Append(char.ToLowerInvariant(character));
            else
                folded.Append(letters);
        }

        return folded.ToString();
    }

    public static SearchQuery Parse(string? term)
    {
        var folded = Fold(term);
        if (folded.Length == 0)
            return SearchQuery.Empty;

        var tokens = folded
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTokens)
            .ToList();
        if (!tokens.Any(token => token.Length >= MinQueryLength))
            return SearchQuery.Empty;

        return new SearchQuery(tokens, BookingCodePrefix(term));
    }

    /// <summary>
    /// The beginning of a booking code the term can be (<see cref="BookingCodes"/>: ten characters of an alphabet without
    /// I, L, O and U, shown as <c>XXXXX-XXXXX</c>): spaces and dashes removed, upper case, and what a person reads wrongly
    /// corrected as <see cref="BookingCodes.TryNormalize"/> does (O as 0, I and L as 1). Null when the term has more than two
    /// parts, a character the alphabet has not, fewer than <see cref="MinBookingCodeLength"/> characters or more than a code. A
    /// term with no digit is taken for a code only when it is a whole one: a name made of the letters of the alphabet ("mario" reads
    /// "MAR10" once O and I are corrected) is not looked up as the beginning of a code.
    /// </summary>
    public static string? BookingCodePrefix(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return null;

        var parts = term.Split([' ', '-', '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', '\u2212'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > MaxBookingCodeParts)
            return null;

        var prefix = new StringBuilder(BookingCodes.Length);
        var hasDigit = false;
        foreach (var part in parts)
        {
            foreach (var raw in part)
            {
                hasDigit |= raw is >= '0' and <= '9';
                var upper = char.ToUpperInvariant(raw);
                var character = upper switch
                {
                    'O' => '0',
                    'I' or 'L' => '1',
                    _ => upper,
                };
                if (BookingCodes.Alphabet.IndexOf(character) < 0 || prefix.Length == BookingCodes.Length)
                    return null;

                prefix.Append(character);
            }
        }

        if (prefix.Length < MinBookingCodeLength)
            return null;

        return hasDigit || prefix.Length == BookingCodes.Length ? prefix.ToString() : null;
    }

    public static bool KeyMatches(string? key, IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrEmpty(key) || tokens.Count == 0)
            return false;

        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var found = false;
            foreach (var word in words)
            {
                if (word.StartsWith(token, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}

/// <summary>
/// A search term ready to run: the words to find at the beginning of a word, and the beginning of a booking code when the term can
/// be one. Built by <see cref="SearchText.Parse"/>, never from a string a client could shape into SQL: a word is only
/// <c>a-z0-9</c>, so the text of a full-text query (<see cref="ToTsQuery"/>) is always valid.
/// </summary>
/// <param name="Tokens">The words, folded, in the order typed, without repeats.</param>
/// <param name="BookingCodePrefix">The beginning of a booking code (stored form), or null.</param>
public sealed record SearchQuery(IReadOnlyList<string> Tokens, string? BookingCodePrefix)
{
    public static SearchQuery Empty { get; } = new([], null);

    public bool IsEmpty => Tokens.Count == 0;

    public string ToTsQuery() => string.Join(" & ", Tokens.Select(token => token + ":*"));
}
