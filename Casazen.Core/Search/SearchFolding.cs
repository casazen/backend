namespace Casazen.Core.Search;

/// <summary>
/// The one table that says how a text is folded for the global search (UI-13a): accents and case are dropped, so "Forli" with
/// its accent, "FORLI" and "forli" are the same word. It is the single source of two things that have to agree to the letter:
/// the C# folding (<see cref="SearchText.Fold"/>, which folds the term typed by the user) and the SQL expression of the stored
/// generated column <c>SearchKey</c> (<c>SearchKeyModel</c> in the infrastructure, which folds the text of the rows in the
/// database). A character that is not here and is not an ASCII letter or digit is not part of a word of the search: it
/// separates words, in C# and in SQL alike.
/// </summary>
/// <remarks>
/// <para>Why a table and not <c>unaccent</c> or the Unicode decomposition: the extension is not available where the application
/// runs (several environments share one Supabase database, one schema each, so an extension has no safe place to live: see
/// <c>docs/runbooks/global-search.md</c>), and a decomposition done in .NET cannot be repeated by the database. This table is
/// plain <c>translate</c> and <c>replace</c> calls, which every PostgreSQL has and which are immutable, as a generated column
/// requires.</para>
/// <para>What is in it: the letters of Latin-1 Supplement and Latin Extended-A, the two Romanian letters with a comma below, and
/// the Angstrom, Kelvin and dotted capital I signs (whose lower case is an ASCII letter in some locales: the database would fold
/// them, so the table must). A script it does not know (Cyrillic, Greek, Arabic, CJK) is not searchable by name in the palette;
/// the lists of the application still have it. Every entry is written as a code point escape so that no character can be
/// confused with another that looks the same (the Kelvin sign is not a K), and <c>SearchFoldingTests</c> checks each one against
/// the Unicode decomposition.</para>
/// </remarks>
public static class SearchFolding
{
    /// <summary>Characters that fold to one plain letter: the characters (code point escapes), and the lower-case ASCII letter they become.</summary>
    private static readonly (string Characters, char Letter)[] Letters =
    [
        // a: Latin-1 vowels with accents, macron, breve, ogonek, caron; and the Angstrom sign U+212B
        (
            "\u00C0\u00C1\u00C2\u00C3\u00C4\u00C5\u00E0\u00E1"
            + "\u00E2\u00E3\u00E4\u00E5\u0100\u0101\u0102\u0103"
            + "\u0104\u0105\u01CD\u01CE\u01FA\u01FB\u212B",
            'a'),

        // c: cedilla, acute, circumflex, dot above, caron
        (
            "\u00C7\u00E7\u0106\u0107\u0108\u0109\u010A\u010B"
            + "\u010C\u010D",
            'c'),

        // d: eth, caron, stroke
        (
            "\u00D0\u00F0\u010E\u010F\u0110\u0111",
            'd'),

        // e: grave, acute, circumflex, diaeresis, macron, breve, dot above, ogonek, caron
        (
            "\u00C8\u00C9\u00CA\u00CB\u00E8\u00E9\u00EA\u00EB"
            + "\u0112\u0113\u0114\u0115\u0116\u0117\u0118\u0119"
            + "\u011A\u011B",
            'e'),

        // g: circumflex, breve, dot above, cedilla
        (
            "\u011C\u011D\u011E\u011F\u0120\u0121\u0122\u0123",
            'g'),

        // h: circumflex, stroke
        (
            "\u0124\u0125\u0126\u0127",
            'h'),

        // i: grave, acute, circumflex, diaeresis, tilde, macron, breve, ogonek, caron; the dotted capital I U+0130 and the dotless i U+0131
        (
            "\u00CC\u00CD\u00CE\u00CF\u00EC\u00ED\u00EE\u00EF"
            + "\u0128\u0129\u012A\u012B\u012C\u012D\u012E\u012F"
            + "\u0130\u0131\u01CF\u01D0",
            'i'),

        // j: circumflex
        (
            "\u0134\u0135",
            'j'),

        // k: cedilla, kra U+0138, and the Kelvin sign U+212A
        (
            "\u0136\u0137\u0138\u212A",
            'k'),

        // l: acute, cedilla, caron, middle dot, stroke
        (
            "\u0139\u013A\u013B\u013C\u013D\u013E\u013F\u0140"
            + "\u0141\u0142",
            'l'),

        // n: tilde, acute, cedilla, caron, apostrophe n U+0149, eng
        (
            "\u00D1\u00F1\u0143\u0144\u0145\u0146\u0147\u0148"
            + "\u0149\u014A\u014B",
            'n'),

        // o: grave, acute, circumflex, tilde, diaeresis, slash, macron, breve, double acute, caron, slash-acute
        (
            "\u00D2\u00D3\u00D4\u00D5\u00D6\u00D8\u00F2\u00F3"
            + "\u00F4\u00F5\u00F6\u00F8\u014C\u014D\u014E\u014F"
            + "\u0150\u0151\u01D1\u01D2\u01FE\u01FF",
            'o'),

        // r: acute, cedilla, caron
        (
            "\u0154\u0155\u0156\u0157\u0158\u0159",
            'r'),

        // s: acute, circumflex, cedilla, caron, long s U+017F, comma below U+0218 U+0219
        (
            "\u015A\u015B\u015C\u015D\u015E\u015F\u0160\u0161"
            + "\u017F\u0218\u0219",
            's'),

        // t: cedilla, caron, stroke, comma below U+021A U+021B
        (
            "\u0162\u0163\u0164\u0165\u0166\u0167\u021A\u021B",
            't'),

        // u: grave, acute, circumflex, diaeresis, tilde, macron, breve, ring, double acute, ogonek, caron
        (
            "\u00D9\u00DA\u00DB\u00DC\u00F9\u00FA\u00FB\u00FC"
            + "\u0168\u0169\u016A\u016B\u016C\u016D\u016E\u016F"
            + "\u0170\u0171\u0172\u0173\u01D3\u01D4",
            'u'),

        // w: circumflex
        (
            "\u0174\u0175",
            'w'),

        // y: acute, diaeresis, circumflex
        (
            "\u00DD\u00FD\u00FF\u0176\u0177\u0178",
            'y'),

        // z: acute, dot above, caron
        (
            "\u0179\u017A\u017B\u017C\u017D\u017E",
            'z'),
    ];

    /// <summary>Characters that fold to two letters: sharp s (U+00DF) and the capital sharp s (U+1E9E), the ligatures AE and OE.</summary>
    private static readonly (char Character, string Letters)[] Pairs =
    [
        ('\u00DF', "ss"),
        ('\u1E9E', "ss"),
        ('\u00C6', "ae"),
        ('\u00E6', "ae"),
        ('\u0152', "oe"),
        ('\u0153', "oe"),
    ];

    private static readonly Dictionary<char, string> Folded = Build();

    /// <summary>
    /// The second argument of the SQL <c>translate(text, from, to)</c>: every character that folds to one letter.
    /// <see cref="TranslateTo"/> has exactly as many characters (a shorter one would make <c>translate</c> delete the rest).
    /// </summary>
    public static string TranslateFrom { get; } = string.Concat(Letters.Select(group => group.Characters));

    /// <summary>The letter each character of <see cref="TranslateFrom"/> becomes, at the same position.</summary>
    public static string TranslateTo { get; } = string.Concat(Letters.Select(group => new string(group.Letter, group.Characters.Length)));

    /// <summary>The characters that fold to two letters (SQL <c>replace(text, character, letters)</c>, one call each).</summary>
    public static IReadOnlyList<(char Character, string Letters)> PairReplacements => Pairs;

    /// <summary>
    /// The folded form of <paramref name="character"/> when it is in the table (one or two lower-case ASCII letters); false for
    /// anything else: an ASCII letter or digit (kept as it is, in lower case) or a separator.
    /// </summary>
    public static bool TryFold(char character, out string folded) => Folded.TryGetValue(character, out folded!);

    private static Dictionary<char, string> Build()
    {
        var map = new Dictionary<char, string>();
        foreach (var (characters, letter) in Letters)
        {
            foreach (var character in characters)
                map.Add(character, letter.ToString());
        }

        foreach (var (character, letters) in Pairs)
            map.Add(character, letters);

        return map;
    }
}
