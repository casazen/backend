using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Search;
using Casazen.Infrastructure.Search;
using Xunit;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// UI-13a: the table that folds accents for the search is right, complete for the range it claims, and the SQL expression built from
/// it (the generated column of the database) gives the same words as <see cref="SearchText.Fold"/>. The expression is run here in C#
/// step by step as PostgreSQL runs it (<c>translate</c>, <c>replace</c>, <c>lower</c>, <c>regexp_replace</c>, <c>btrim</c>); a
/// PostgreSQL test compares it with the real database (<c>GlobalSearchPostgresTests</c>).
/// </summary>
public class SearchFoldingTests
{
    [Fact]
    public void Table_TranslateFromAndTo_HaveTheSameLength_AndNoCharacterTwice()
    {
        // translate() deletes the characters of the first list that have no partner in the second, and uses the first occurrence of a repeated one.
        Assert.Equal(SearchFolding.TranslateFrom.Length, SearchFolding.TranslateTo.Length);
        Assert.Equal(SearchFolding.TranslateFrom.Length, SearchFolding.TranslateFrom.Distinct().Count());
        Assert.All(SearchFolding.TranslateTo, letter => Assert.True(letter is >= 'a' and <= 'z'));
        Assert.All(SearchFolding.TranslateFrom, character => Assert.True(character > 127, $"U+{(int)character:X4} is ASCII"));
    }

    [Fact]
    public void Table_APairIsNotAlsoASingleLetter()
    {
        var singles = SearchFolding.TranslateFrom.ToHashSet();

        Assert.All(SearchFolding.PairReplacements, pair => Assert.DoesNotContain(pair.Character, singles));
        Assert.All(SearchFolding.PairReplacements, pair => Assert.Equal(2, pair.Letters.Length));
    }

    [Fact]
    public void Table_EveryLetterIsFoldedToTheLetterItIsBuiltOn()
    {
        var wrong = new List<string>();
        for (var index = 0; index < SearchFolding.TranslateFrom.Length; index++)
        {
            var character = SearchFolding.TranslateFrom[index];
            var target = SearchFolding.TranslateTo[index];
            var expected = Expected(character, character.ToString().Normalize(NormalizationForm.FormD));

            if (expected != target)
                wrong.Add($"U+{(int)character:X4} folds to '{target}', the Unicode decomposition says '{expected}'");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

        static char Expected(char character, string decomposed)
        {
            // Letters that Unicode does not decompose (stroke, eth, dotless i, signs...): the letter they are a variant of, by code point.
            return (int)character switch
            {
                0x00D0 or 0x00F0 or 0x0110 or 0x0111 => 'd',
                0x0126 or 0x0127 => 'h',
                0x0130 or 0x0131 => 'i',
                0x0138 or 0x212A => 'k',
                0x013F or 0x0140 or 0x0141 or 0x0142 => 'l',
                0x0149 or 0x014A or 0x014B => 'n',
                0x00D8 or 0x00F8 or 0x01FE or 0x01FF => 'o',
                0x017F => 's',
                0x0166 or 0x0167 => 't',
                0x212B => 'a',
                _ => char.ToLowerInvariant(decomposed[0]),
            };
        }
    }

    [Fact]
    public void Table_EveryLetterOfLatin1AndLatinExtendedAThatUnicodeDecomposesToALetter_IsInIt()
    {
        var missing = new List<string>();
        for (var code = 0x00C0; code <= 0x017F; code++)
        {
            var character = (char)code;
            if (!char.IsLetter(character))
                continue;

            var decomposed = character.ToString().Normalize(NormalizationForm.FormD);
            var isAccentedAscii = decomposed.Length > 1 && decomposed[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
            if (isAccentedAscii && !SearchFolding.TryFold(character, out _))
                missing.Add($"U+{code:X4} ({decomposed[0]} with an accent)");
        }

        Assert.True(missing.Count == 0, "Add to SearchFolding: " + string.Join(", ", missing));
    }

    [Fact]
    public void Table_TheSignsWhoseLowerCaseIsAsciiAreIn()
    {
        // Kelvin sign, Angstrom sign, dotted capital I: PostgreSQL's lower() could turn them into an ASCII letter after translate().
        foreach (var code in new[] { 0x212A, 0x212B, 0x0130 })
            Assert.True(SearchFolding.TryFold((char)code, out _), $"U+{code:X4}");
    }

    [Fact]
    public void Table_WhatIsNotALetterOfTheTable_IsNotInIt()
    {
        // ASCII; the multiplication and division signs (they sit among the Latin-1 letters); an en dash; the typographic
        // apostrophe; a Cyrillic capital letter.
        foreach (var code in new[] { 'a', 'Z', '0', ' ', '-', 0x00D7, 0x00F7, 0x2013, 0x2019, 0x0419 })
            Assert.False(SearchFolding.TryFold((char)code, out _), $"U+{(int)code:X4}");
    }

    // ─── The SQL expression, run in C# as PostgreSQL runs it ────────────────────────────────────────────

    /// <summary>
    /// The steps of <see cref="SearchKeyModel.FoldSql"/> as PostgreSQL executes them: translate (first occurrence wins, no
    /// partner deletes), replace for the pairs, lower (a UTF-8 locale: every letter), the regular expression, btrim (spaces).
    /// </summary>
    private static string RunTheSql(string input)
    {
        var from = SearchFolding.TranslateFrom;
        var to = SearchFolding.TranslateTo;

        var translated = new StringBuilder();
        foreach (var character in input)
        {
            var index = from.IndexOf(character);
            if (index < 0)
                translated.Append(character);
            else if (index < to.Length)
                translated.Append(to[index]);
        }

        var text = translated.ToString();
        foreach (var (character, letters) in SearchFolding.PairReplacements)
            text = text.Replace(character.ToString(), letters, StringComparison.Ordinal);

        text = text.ToLowerInvariant();
        text = Regex.Replace(text, "[^a-z0-9]+", " ");
        return text.Trim(' ');
    }

    [Fact]
    public void Fold_EveryCharacterOfTheBasicPlane_GivesWhatTheSqlExpressionGives()
    {
        var different = new List<string>();
        for (var code = 0; code <= 0xFFFF; code++)
        {
            if (code is >= 0xD800 and <= 0xDFFF)
                continue;

            var text = "x" + (char)code + "y";
            if (SearchText.Fold(text) != RunTheSql(text))
                different.Add($"U+{code:X4}: C# '{SearchText.Fold(text)}', SQL '{RunTheSql(text)}'");
        }

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different.Take(20)));
    }

    [Theory]
    [InlineData("Forlì")]
    [InlineData("  José   Müller-Weiß ")]
    [InlineData("Dell’Orto - O'Brien")]
    [InlineData("Ærø Œuvre Straße ẞ")]
    [InlineData("Łukasz Żółć İstanbul K Å")]
    [InlineData("IT058091C27G5FFZDZ  mario.rossi@example.it")]
    [InlineData("Иван 中文")]
    [InlineData("")]
    public void Fold_Samples_GiveWhatTheSqlExpressionGives(string text)
    {
        Assert.Equal(RunTheSql(text), SearchText.Fold(text));
    }

    [Fact]
    public void Fold_TheSamplesOfTheDatabaseTests_GiveTheWordsWrittenByHand()
    {
        // The same texts and words as GlobalSearchPostgresTests compares with the generated columns of a real database.
        foreach (var sample in SearchFoldingSamples.Guests)
        {
            Assert.Equal(sample.Key, SearchText.Fold(sample.Text));
            Assert.Equal(sample.Key, RunTheSql(sample.Text));
        }

        foreach (var sample in SearchFoldingSamples.Properties)
        {
            Assert.Equal(sample.Key, SearchText.Fold(sample.Text));
            Assert.Equal(sample.Key, RunTheSql(sample.Text));
        }
    }

    [Fact]
    public void FoldSql_IsPureAscii_ChainsTheSixPairsAndNamesTheFunctionsOnce()
    {
        var sql = SearchKeyModel.FoldSql("\"Name\"");

        Assert.All(sql, character => Assert.True(character < 128, $"U+{(int)character:X4} in the SQL"));
        Assert.StartsWith("btrim(regexp_replace(lower(", sql, StringComparison.Ordinal);
        Assert.EndsWith(", '[^a-z0-9]+', ' ', 'g'))", sql, StringComparison.Ordinal);
        Assert.Equal(1, Count(sql, "translate("));
        Assert.Equal(SearchFolding.PairReplacements.Count, Regex.Matches(sql, @"(?<![A-Za-z_])replace\(").Count);
        Assert.Equal(Count(sql, "("), Count(sql, ")"));
        Assert.Equal(1, Count(sql, "\"Name\""));
    }

    [Fact]
    public void FoldSql_TheListsOfTranslateAreUnicodeEscapeStrings_OfTheSameLength()
    {
        var sql = SearchKeyModel.FoldSql("\"Name\"");
        var from = Regex.Match(sql, @"U&'((?:\\[0-9A-F]{4})+)'");
        var to = Regex.Match(sql, @"translate\(""Name"", U&'[^']*', '([a-z]+)'\)");

        Assert.True(from.Success && to.Success, sql);
        Assert.Equal(SearchFolding.TranslateFrom.Length, from.Groups[1].Value.Length / 5);
        Assert.Equal(SearchFolding.TranslateTo, to.Groups[1].Value);

        var decoded = new string(Regex.Matches(from.Groups[1].Value, @"\\([0-9A-F]{4})")
            .Select(match => (char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray());
        Assert.Equal(SearchFolding.TranslateFrom, decoded);
    }

    [Fact]
    public void FoldSql_ThePairsAreReplacedByTheirLetters()
    {
        var sql = SearchKeyModel.FoldSql("\"Name\"");

        foreach (var (character, letters) in SearchFolding.PairReplacements)
            Assert.Contains($", U&'\\{(int)character:X4}', '{letters}')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldSql_RefusesNoText()
    {
        Assert.Throws<ArgumentException>(() => SearchKeyModel.FoldSql(" "));
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
