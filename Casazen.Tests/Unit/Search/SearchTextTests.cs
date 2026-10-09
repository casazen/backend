using Casazen.Core.Search;
using Xunit;

namespace Casazen.Tests.Unit.Search;

/// <summary>UI-13a: how a text is folded and a term is read, the same rules the database applies to the stored keys.</summary>
public class SearchTextTests
{
    [Theory]
    [InlineData("Forlì", "forli")]
    [InlineData("FORLÌ", "forli")]
    [InlineData("  José   Müller-Weiß ", "jose muller weiss")]
    [InlineData("O'Brien", "o brien")]
    [InlineData("Dell’Orto", "dell orto")]
    [InlineData("mario.rossi@example.it", "mario rossi example it")]
    [InlineData("IT058091C27G5FFZDZ", "it058091c27g5ffzdz")]
    [InlineData("Łukasz Żółć", "lukasz zolc")]
    [InlineData("Ærø Œuvre Straße", "aero oeuvre strasse")]
    [InlineData("Ștefan Țepeș", "stefan tepes")]
    [InlineData("São Tomé", "sao tome")]
    [InlineData("Ñandú", "nandu")]
    [InlineData("A1-B2", "a1 b2")]
    public void Fold_LowerCaseNoAccentsWordsOfAsciiSeparatedByOneSpace(string text, string expected)
    {
        Assert.Equal(expected, SearchText.Fold(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!?-_.,;")]
    public void Fold_NothingToKeep_IsEmpty(string? text)
    {
        Assert.Equal(string.Empty, SearchText.Fold(text));
    }

    [Theory]
    [InlineData("Иван")] // Cyrillic: not a script of the table, so a separator
    [InlineData("中文")]
    public void Fold_AScriptTheTableDoesNotKnow_IsNotSearchable(string text)
    {
        Assert.Equal(string.Empty, SearchText.Fold(text));
        Assert.True(SearchText.Parse(text).IsEmpty);
    }

    [Fact]
    public void Fold_TheKelvinAndAngstromSignsAndTheDottedI_AreLetters()
    {
        // Their lower case is an ASCII letter in some locales, so the database would fold them: the table must too.
        Assert.Equal("k", SearchText.Fold("K"));
        Assert.Equal("a", SearchText.Fold("Å"));
        Assert.Equal("istanbul", SearchText.Fold("İstanbul"));
    }

    [Fact]
    public void Fold_IsIdempotent_AndNeverHasDoubleSpacesOrSpacesAtTheEnds()
    {
        var samples = new[] { "  José   Müller ", "a - b", "\tCasa\n\nBella  ", "x", "Straße--Ærø" };

        foreach (var sample in samples)
        {
            var folded = SearchText.Fold(sample);
            Assert.Equal(folded, SearchText.Fold(folded));
            Assert.DoesNotContain("  ", folded, StringComparison.Ordinal);
            Assert.Equal(folded.Trim(), folded);
            Assert.All(folded, character => Assert.True(character is ' ' or (>= 'a' and <= 'z') or (>= '0' and <= '9')));
        }
    }

    // ─── Parse ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TheWordsInTheOrderTyped_FoldedWithoutRepeats()
    {
        var query = SearchText.Parse("  Rossi  MARIA rossi ");

        Assert.Equal(["rossi", "maria"], query.Tokens);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void Parse_NoMoreThanFiveWords()
    {
        var query = SearchText.Parse("uno due tre quattro cinque sei sette");

        Assert.Equal(["uno", "due", "tre", "quattro", "cinque"], query.Tokens);
        Assert.Equal(SearchText.MaxTokens, query.Tokens.Count);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a b c")]
    [InlineData("!!")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_NoWordOfAtLeastTwoCharacters_IsEmpty(string? term)
    {
        Assert.True(SearchText.Parse(term).IsEmpty);
        Assert.Same(SearchQuery.Empty, SearchText.Parse(term));
    }

    [Fact]
    public void Parse_AOneLetterWordNextToALongerOne_IsKept()
    {
        // "via a" is a way to say "via a ..." : the short word narrows the result, the long one makes the term safe to run.
        Assert.Equal(["a", "rossi"], SearchText.Parse("a rossi").Tokens);
    }

    [Fact]
    public void ToTsQuery_EachWordAsAPrefix_JoinedByAnd_AlwaysAValidQuery()
    {
        Assert.Equal("ros:* & mar:*", SearchText.Parse("ros mar").ToTsQuery());
        Assert.Equal("it0720:*", SearchText.Parse("IT0720").ToTsQuery());

        // Whatever is typed, the text holds only a-z, 0-9, ':*' and ' & ': nothing a client could shape into the query language.
        foreach (var hostile in new[] { "ros' | !mar", "a:*&b", "(x) <-> y", "\\", "%_", "ros\u0000mar", "foo\"bar" })
        {
            var text = SearchText.Parse(hostile).ToTsQuery();
            Assert.Matches("^([a-z0-9]+:\\*)( & [a-z0-9]+:\\*)*$|^$", text);
        }
    }

    // ─── Booking code ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("7K3M9-PQ2XV", "7K3M9PQ2XV")]
    [InlineData("7k3m9pq2xv", "7K3M9PQ2XV")]
    [InlineData("7K3M9 PQ2XV", "7K3M9PQ2XV")]
    [InlineData("7K3M9–PQ2XV", "7K3M9PQ2XV")]
    [InlineData("7K3M", "7K3M")]
    [InlineData("7k3m9-pq", "7K3M9PQ")]
    [InlineData("A1B2C3", "A1B2C3")]
    public void BookingCodePrefix_TheFormShownToPeopleOrAnyBeginningOfIt(string term, string expected)
    {
        Assert.Equal(expected, SearchText.BookingCodePrefix(term));
    }

    [Theory]
    [InlineData("O1L")] // O and L read as 0 and 1, but only three characters
    [InlineData("7K3")]
    [InlineData("7K3U9")] // U is not in the alphabet
    [InlineData("7K3M9-PQ2XV-12")] // three parts
    [InlineData("7K3M9PQ2XV1")] // more than a code
    [InlineData("zxcv")] // letters only and not whole
    [InlineData("mario")]
    [InlineData("rossi")]
    [InlineData("")]
    [InlineData(null)]
    public void BookingCodePrefix_WhatCannotBeTheBeginningOfACode_IsNull(string? term)
    {
        Assert.Null(SearchText.BookingCodePrefix(term));
    }

    [Fact]
    public void BookingCodePrefix_ACodeWithNoDigitIsStillFoundWhole()
    {
        // 2.4 % of the codes have no digit; typed whole (10 characters) they are looked up.
        Assert.Equal("ABCDEFGHJK", SearchText.BookingCodePrefix("ABCDE-FGHJK"));
    }

    [Fact]
    public void BookingCodePrefix_OAndIAreReadAsZeroAndOne_LikeBookingCodesDoes()
    {
        Assert.Equal("0123456781", SearchText.BookingCodePrefix("O1 2345678L"));
        Assert.True(Casazen.Core.Services.BookingCodes.TryNormalize("O1 2345678-L", out var whole));
        Assert.Equal("0123456781", whole);
    }

    // ─── KeyMatches ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rossi maria", new[] { "ros" }, true)]
    [InlineData("rossi maria", new[] { "mar", "ros" }, true)]
    [InlineData("rossi maria", new[] { "maria", "rossi" }, true)]
    [InlineData("rossi maria", new[] { "ssi" }, false)]
    [InlineData("rossi maria", new[] { "ros", "xyz" }, false)]
    [InlineData("carlo sorrosi", new[] { "ros" }, false)]
    [InlineData("de rossi maria", new[] { "rossi" }, true)]
    [InlineData("it058091c27g5ffzdz roma", new[] { "it0580" }, true)]
    [InlineData("", new[] { "ros" }, false)]
    [InlineData("rossi", new string[0], false)]
    public void KeyMatches_EveryWordIsTheBeginningOfAWordOfTheKey(string key, string[] tokens, bool expected)
    {
        Assert.Equal(expected, SearchText.KeyMatches(key, tokens));
    }

    [Fact]
    public void KeyMatches_ANullKey_IsNoMatch()
    {
        Assert.False(SearchText.KeyMatches(null, ["ros"]));
    }
}
