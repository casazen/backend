namespace Casazen.Tests.Unit.Search;

/// <summary>
/// A guest whose texts are hard for the folding, and the key (the words) the search must store for it: surname, first name and
/// e-mail address, folded. The expected words are written by hand, so that neither the C# folding nor the SQL expression is
/// compared with itself: <c>SearchFoldingTests</c> checks them against <c>SearchText.Fold</c> and the steps of the SQL, and
/// <c>GlobalSearchPostgresTests</c> against what the database computes.
/// </summary>
internal sealed record GuestFoldingSample(string FirstName, string LastName, string Email, string Key)
{
    /// <summary>The text the key is made of, in the order of the generated column.</summary>
    public string Text => $"{LastName} {FirstName} {Email}";
}

/// <summary>A property whose texts are hard for the folding: name, city and CIN, and the key the search must store for it.</summary>
internal sealed record PropertyFoldingSample(string Name, string City, string? CinCode, string Key)
{
    /// <summary>The text the key is made of, in the order of the generated column.</summary>
    public string Text => $"{Name} {City} {CinCode}";
}

internal static class SearchFoldingSamples
{
    /// <summary>
    /// Everything the expression has to fold: accents of both cases, the sharp s and the ligatures that become two letters, an
    /// apostrophe and a typographic one, digits inside a word, the signs whose lower case is an ASCII letter (Kelvin, Angstrom,
    /// the dotted capital I), a script the table does not know, a plus sign in an e-mail address, runs of spaces and a tab.
    /// Written with escapes so that no editor or encoding can change them.
    /// </summary>
    public static IReadOnlyList<GuestFoldingSample> Guests { get; } =
    [
        // e with diaeresis; sharp s.
        new("Zo\u00EB", "O'Neill-Wei\u00DF", "Zoe.ONeill@Example.COM", "o neill weiss zoe zoe oneill example com"),
        // L with stroke; Z with dot above, o acute, l with stroke, c acute.
        new("\u0141ukasz", "\u017B\u00F3\u0142\u0107", "l.zolc@example.pl", "zolc lukasz l zolc example pl"),
        // AE; OE, sharp s and the capital sharp s.
        new("\u00C6sir", "\u0152uvre Stra\u00DFe \u1E9E", "aesir@example.is", "oeuvre strasse ss aesir aesir example is"),
        // The dotted capital I; U with diaeresis, the Kelvin sign and the Angstrom sign.
        new("\u0130stanbul", "\u00DCnal \u212A \u212B", "unal@example.tr", "unal k a istanbul unal example tr"),
        // A Cyrillic first name (no word at all), digits inside a word, a typographic apostrophe.
        new("\u0418\u0432\u0430\u043D", "R2D2 Dell\u2019Orto", "ivan@example.ru", "r2d2 dell orto ivan example ru"),
        new("  Spazi   Multipli  ", "\t", "s@example.com", "spazi multipli s example com"),
        new("ROBERT", "McDONALD", "R.McDonald+tag@Example.Co.UK", "mcdonald robert r mcdonald tag example co uk"),
    ];

    public static IReadOnlyList<PropertyFoldingSample> Properties { get; } =
    [
        // E grave.
        new("Casa d'\u00C8lite n.5", "Sant'Angelo", "IT058091C27G5FFZDZ", "casa d elite n 5 sant angelo it058091c27g5ffzdz"),
        // E grave, A grave; S with comma below (Romanian).
        new("\u00C8 - \u00C0 la page", "\u0218tefan cel Mare", null, "e a la page stefan cel mare"),
    ];
}
